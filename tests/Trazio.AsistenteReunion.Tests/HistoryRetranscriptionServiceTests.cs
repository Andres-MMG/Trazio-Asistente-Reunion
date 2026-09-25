using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Trazio.AsistenteReunion.App;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.Tests;

public sealed class HistoryRetranscriptionServiceTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "trazio-retranscription-" + Guid.NewGuid().ToString("N"));
    private AesContentProtector _protector = null!;
    private SqliteSessionStore _store = null!;
    private AudioArchiveStore _archive = null!;
    private string _model = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _protector = new(RandomNumberGenerator.GetBytes(32));
        _store = new(Path.Combine(_root, "test.db"), _protector);
        await _store.InitializeAsync();
        _archive = new(Path.Combine(_root, "audio"), _store, _protector);
        _model = Path.Combine(_root, "model.bin");
        await File.WriteAllBytesAsync(_model, [1, 2, 3, 4]);
    }

    public Task DisposeAsync() { _protector.Dispose(); Directory.Delete(_root, true); return Task.CompletedTask; }

    [Fact]
    public async Task RunAsync_CompletedSession_StreamsChunksInOrderAndCreatesImmutableSuccessfulRevision()
    {
        var session = await CreateSessionAsync(SessionState.Completed);
        await WriteAudioAsync(session, AudioSourceKind.Microphone, 31);
        await _store.SaveSegmentAsync(new("original", session.Id, AudioSourceKind.Microphone, 0, TimeSpan.Zero, TimeSpan.FromSeconds(1), "immutable original", DateTimeOffset.UtcNow));
        var transport = new FakeTransportFactory();
        var service = new HistoryRetranscriptionService(_store, _archive, transport);

        var revision = await service.RunAsync(ToSummary(session), AudioSourceKind.Microphone, _model, "es");

        var revisions = await _store.ListModelRevisionsAsync(session.Id, AudioSourceKind.Microphone);
        var segments = await _store.GetModelRevisionSegmentsAsync(revision.Id);
        Assert.Equal(ModelRevisionStatus.Succeeded, Assert.Single(revisions).Status);
        Assert.Equal("FAKE-VERIFIED-HASH", revision.ModelHash);
        Assert.Null(revision.Scope);
        Assert.Equal([0, 1], transport.Transport!.Calls);
        Assert.Equal(2, segments.Count);
        Assert.Equal(TimeSpan.Zero, segments[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(30), segments[1].Start);
        Assert.Equal("immutable original", Assert.Single(await _store.GetSegmentsAsync(session.Id)).Text);
        Assert.DoesNotContain("generated-0", System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(Path.Combine(_root, "test.db"))));
        Assert.Empty(Directory.GetFiles(_root, "*.wav", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task RunSelectedSegmentAsync_CrossesTwoEncryptedChunksAndStoresPartialScope()
    {
        var session = await CreateSessionAsync(SessionState.Completed);
        var source = AudioSourceKind.Microphone;
        await using (var writer = _archive.CreateSession(session.Id, long.MaxValue))
        {
            await writer.AppendAsync(new(source, Enumerable.Repeat((byte)0x11, 30 * 32_000).ToArray(), session.StartedAt));
            await writer.AppendAsync(new(source, Enumerable.Repeat((byte)0x22, 30 * 32_000).ToArray(), session.StartedAt.AddSeconds(30)));
            await writer.CompleteAsync();
        }
        var original = new TranscriptSegment("selected-original", session.Id, source, 0,
            TimeSpan.FromSeconds(29), TimeSpan.FromSeconds(31), "texto aprobado", DateTimeOffset.UtcNow);
        await _store.SaveSegmentAsync(original);
        var factory = new IntervalTransportFactory();
        var service = new HistoryRetranscriptionService(_store, _archive, factory);

        var revision = await service.RunSelectedSegmentAsync(ToSummary(session), original, _model, "es");
        await _store.InitializeAsync();
        var restored = Assert.Single(await _store.ListModelRevisionsAsync(session.Id, source, true));
        var segment = Assert.Single(await _store.GetModelRevisionSegmentsAsync(revision.Id));

        Assert.Equal(new TranscriptModelRevisionScope(original.Id, original.Start, original.End), restored.Scope);
        Assert.Equal("FAKE-INTERVAL-HASH", restored.ModelHash);
        Assert.Equal(ModelRevisionProducer.Qwen3AsrLlamaCppV1, restored.ProducerIdentity);
        Assert.Equal(2 * 32_000, factory.Pcm!.Length);
        Assert.All(factory.Pcm.AsSpan(0, 32_000).ToArray(), value => Assert.Equal(0x11, value));
        Assert.All(factory.Pcm.AsSpan(32_000).ToArray(), value => Assert.Equal(0x22, value));
        Assert.Equal(original.Start, segment.Start);
        Assert.Equal(original.Start + TimeSpan.FromMilliseconds(500), segment.End);
        Assert.Equal("texto aprobado", Assert.Single(await _store.GetSegmentsAsync(session.Id)).Text);
    }

    [Fact]
    public async Task RunSelectedSegmentAsync_DeniedQwenConsent_NeverDecryptsAudioOrStartsProcess()
    {
        var session = await CreateSessionAsync(SessionState.Completed);
        await WriteAudioAsync(session, AudioSourceKind.Microphone, 1);
        var original = new TranscriptSegment("consent-denied", session.Id, AudioSourceKind.Microphone,
            0, TimeSpan.Zero, TimeSpan.FromSeconds(1), "texto original", DateTimeOffset.UtcNow);
        await _store.SaveSegmentAsync(original);
        var executable = Path.Combine(_root, "llama-server.exe");
        var projector = Path.Combine(_root, "mmproj.gguf");
        await File.WriteAllBytesAsync(executable, [1, 2, 3]);
        await File.WriteAllBytesAsync(projector, [4, 5, 6]);
        var counter = new CountingProtector(_protector);
        var readArchive = new AudioArchiveStore(Path.Combine(_root, "audio"), _store, counter);
        var consentCalls = 0;
        var factory = new LlamaCppAsrTransportFactory(
            LlamaCppAsrRuntimeOptions.Create(executable, projector),
            (_, _) => { consentCalls++; return Task.FromResult(false); });
        var service = new HistoryRetranscriptionService(_store, readArchive, factory);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.RunSelectedSegmentAsync(ToSummary(session), original, _model, "es"));

        Assert.Equal(1, consentCalls);
        Assert.Equal(0, counter.UnprotectCalls);
        Assert.Empty(await _store.ListModelRevisionsAsync(session.Id));
    }

    [Fact]
    public async Task RunSelectedSegmentAsync_SelectionCancelledDuringConsent_NeverDecryptsAudioOrStartsProcess()
    {
        var session = await CreateSessionAsync(SessionState.Completed);
        await WriteAudioAsync(session, AudioSourceKind.Microphone, 1);
        var original = new TranscriptSegment("selection-stale", session.Id, AudioSourceKind.Microphone,
            0, TimeSpan.Zero, TimeSpan.FromSeconds(1), "texto original", DateTimeOffset.UtcNow);
        await _store.SaveSegmentAsync(original);
        var executable = Path.Combine(_root, "llama-server.exe");
        var projector = Path.Combine(_root, "mmproj.gguf");
        await File.WriteAllBytesAsync(executable, [1, 2, 3]);
        await File.WriteAllBytesAsync(projector, [4, 5, 6]);
        var counter = new CountingProtector(_protector);
        var readArchive = new AudioArchiveStore(Path.Combine(_root, "audio"), _store, counter);
        using var cancellation = new CancellationTokenSource();
        var factory = new LlamaCppAsrTransportFactory(
            LlamaCppAsrRuntimeOptions.Create(executable, projector),
            (_, _) => { cancellation.Cancel(); return Task.FromResult(true); });
        var service = new HistoryRetranscriptionService(_store, readArchive, factory);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.RunSelectedSegmentAsync(ToSummary(session), original, _model, "es", cancellation.Token));

        Assert.Equal(0, counter.UnprotectCalls);
        Assert.Empty(await _store.ListModelRevisionsAsync(session.Id));
    }

    [Fact]
    public async Task LegacySegmentWithoutTicks_UsesStoredMillisecondsWithoutReinterpretation()
    {
        var session = await CreateSessionAsync(SessionState.Completed);
        var original = new TranscriptSegment("legacy-ms", session.Id, AudioSourceKind.Microphone,
            0, TimeSpan.FromMilliseconds(1001), TimeSpan.FromMilliseconds(2001),
            "texto legado", DateTimeOffset.UtcNow);
        await _store.SaveSegmentAsync(original);
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(_root, "test.db"), Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE segments SET start_ticks=NULL,end_ticks=NULL WHERE id=$id";
            command.Parameters.AddWithValue("$id", original.Id);
            await command.ExecuteNonQueryAsync();
        }
        var restored = Assert.Single(await _store.GetSegmentsAsync(session.Id));
        Assert.Equal(original.Start, restored.Start);
        Assert.Equal(original.End, restored.End);
        var revision = await _store.StartSegmentModelRevisionAsync(session.Id,
            AudioSourceKind.Microphone, new TranscriptModelRevisionScope(restored.Id, restored.Start, restored.End),
            "legacy-model", null, "es");
        Assert.Equal(original.Start, Assert.Single(await _store.ListModelRevisionsAsync(session.Id)).Scope!.Start);
        Assert.Equal(original.End, revision.Scope!.End);
    }

    [Fact]
    public async Task RunSelectedSegmentAsync_SubMillisecondArchiveAnchorRoundTripsExactScope()
    {
        var session = await CreateSessionAsync(SessionState.Completed);
        var offset = TimeSpan.FromTicks(10_008_000);
        await using (var writer = _archive.CreateSession(session.Id, long.MaxValue))
        {
            await writer.AppendAsync(new(AudioSourceKind.Microphone, new byte[32_000],
                session.StartedAt.Add(offset), "run-a", 0, 0));
            await writer.CompleteAsync();
        }
        var original = new TranscriptSegment("submillisecond-original", session.Id,
            AudioSourceKind.Microphone, 0, offset, offset + TimeSpan.FromSeconds(1),
            "texto original", DateTimeOffset.UtcNow);
        await _store.SaveSegmentAsync(original);
        await _store.InitializeAsync();
        var restoredOriginal = Assert.Single(await _store.GetSegmentsAsync(session.Id));
        Assert.Equal(original.Start, restoredOriginal.Start);
        Assert.Equal(original.End, restoredOriginal.End);
        var factory = new IntervalTransportFactory();
        var service = new HistoryRetranscriptionService(_store, _archive, factory);

        var revision = await service.RunSelectedSegmentAsync(ToSummary(session), restoredOriginal, _model, "es");

        var restoredRevision = Assert.Single(await _store.ListModelRevisionsAsync(session.Id));
        var revised = Assert.Single(await _store.GetModelRevisionSegmentsAsync(revision.Id));
        Assert.Equal(new TranscriptModelRevisionScope(original.Id, original.Start, original.End), restoredRevision.Scope);
        Assert.Equal(ModelRevisionProducer.Qwen3AsrLlamaCppV1, restoredRevision.ProducerIdentity);
        Assert.Equal(original.Start, revised.Start);
        Assert.Equal(original.Start + TimeSpan.FromMilliseconds(500), revised.End);
        Assert.Equal(32_000, factory.Pcm!.Length);
        Assert.Equal("texto original", Assert.Single(await _store.GetSegmentsAsync(session.Id)).Text);
    }

    [Fact]
    public async Task RunSelectedSegmentAsync_GapOrMissingAudioFailsBeforeStartingRevision()
    {
        var session = await CreateSessionAsync(SessionState.Completed);
        var source = AudioSourceKind.SystemOutput;
        await using (var writer = _archive.CreateSession(session.Id, long.MaxValue))
        {
            await writer.AppendAsync(new(source, new byte[30 * 32_000], session.StartedAt));
            await writer.AppendAsync(new(source, new byte[30 * 32_000], session.StartedAt.AddSeconds(31)));
            await writer.CompleteAsync();
        }
        var original = new TranscriptSegment("gap-original", session.Id, source, 0,
            TimeSpan.FromSeconds(29), TimeSpan.FromSeconds(32), "texto aprobado", DateTimeOffset.UtcNow);
        await _store.SaveSegmentAsync(original);
        var factory = new IntervalTransportFactory();
        var service = new HistoryRetranscriptionService(_store, _archive, factory);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RunSelectedSegmentAsync(ToSummary(session), original, _model, "es"));

        Assert.Null(factory.Pcm);
        Assert.Empty(await _store.ListModelRevisionsAsync(session.Id));
        Assert.Equal("texto aprobado", Assert.Single(await _store.GetSegmentsAsync(session.Id)).Text);
    }

    [Fact]
    public async Task RunAsync_NoAudioOrActiveSession_IsRejectedWithoutRevision()
    {
        var completed = await CreateSessionAsync(SessionState.Completed);
        var active = await CreateSessionAsync(SessionState.Recording);
        await WriteAudioAsync(active, AudioSourceKind.Microphone, 1);
        var service = new HistoryRetranscriptionService(_store, _archive, new FakeTransportFactory());

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunAsync(ToSummary(completed), AudioSourceKind.Microphone, _model, "es"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunAsync(ToSummary(active), AudioSourceKind.Microphone, _model, "es"));
        Assert.Empty(await _store.ListModelRevisionsAsync(completed.Id));
        Assert.Empty(await _store.ListModelRevisionsAsync(active.Id));
    }

    [Fact]
    public async Task RunAsync_WorkerFailure_RemainsDiagnosableAndNotSuccessful()
    {
        var session = await CreateSessionAsync(SessionState.Interrupted);
        await WriteAudioAsync(session, AudioSourceKind.SystemOutput, 1);
        var service = new HistoryRetranscriptionService(_store, _archive, new FakeTransportFactory { Error = "private failure detail" });

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunAsync(ToSummary(session), AudioSourceKind.SystemOutput, _model, "es"));

        var failed = Assert.Single(await _store.ListModelRevisionsAsync(session.Id));
        Assert.Equal(ModelRevisionStatus.Failed, failed.Status);
        Assert.Equal("private failure detail", failed.Error);
        Assert.Empty(await _store.ListModelRevisionsAsync(session.Id, null, successfulOnly: true));
        Assert.DoesNotContain("private failure detail", System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(Path.Combine(_root, "test.db"))));
    }

    [Fact]
    public async Task RunAsync_ProductionPipeConnectTimeout_PersistsEncryptedFailedAttempt()
    {
        var session = await CreateSessionAsync(SessionState.Completed);
        await WriteAudioAsync(session, AudioSourceKind.Microphone, 1);
        var service = new HistoryRetranscriptionService(
            _store,
            _archive,
            new ProductionPipeTimeoutTransportFactory());

        await Assert.ThrowsAsync<TimeoutException>(() =>
            service.RunAsync(ToSummary(session), AudioSourceKind.Microphone, _model, "es"));

        var failed = Assert.Single(await _store.ListModelRevisionsAsync(session.Id));
        Assert.Equal(ModelRevisionStatus.Failed, failed.Status);
        Assert.Contains("Se agotó el tiempo de espera", failed.Error, StringComparison.Ordinal);
        Assert.Empty(await _store.ListModelRevisionsAsync(session.Id, null, successfulOnly: true));
        var raw = System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(Path.Combine(_root, "test.db")));
        Assert.DoesNotContain("Se agotó el tiempo de espera", raw, StringComparison.Ordinal);
    }
    [Theory]
    [InlineData("corrupt model startup detail", false)]
    [InlineData("worker start timeout detail", true)]
    public async Task RunAsync_WorkerStartupFailure_PersistsEncryptedFailedAttemptThatIsNotSelectable(string detail, bool timeout)
    {
        var session = await CreateSessionAsync(SessionState.Completed);
        await WriteAudioAsync(session, AudioSourceKind.Microphone, 1);
        var service = new HistoryRetranscriptionService(
            _store,
            _archive,
            new ThrowingStartTransportFactory(timeout ? new TimeoutException(detail) : new InvalidDataException(detail)));

        var exception = await Assert.ThrowsAnyAsync<Exception>(() =>
            service.RunAsync(ToSummary(session), AudioSourceKind.Microphone, _model, "es"));
        Assert.Equal(detail, exception.Message);

        var failed = Assert.Single(await _store.ListModelRevisionsAsync(session.Id));
        Assert.Equal(ModelRevisionStatus.Failed, failed.Status);
        Assert.Null(failed.ModelHash);
        Assert.Equal(detail, failed.Error);
        Assert.Empty(await _store.ListModelRevisionsAsync(session.Id, null, successfulOnly: true));
        var raw = System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(Path.Combine(_root, "test.db")));
        Assert.DoesNotContain(detail, raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_CancelledDuringWorkerStartup_PersistsCancelledAttemptThatIsNotSelectable()
    {
        var session = await CreateSessionAsync(SessionState.Completed);
        await WriteAudioAsync(session, AudioSourceKind.Microphone, 1);
        var factory = new CancellableStartTransportFactory();
        var service = new HistoryRetranscriptionService(_store, _archive, factory);
        using var cancellation = new CancellationTokenSource();

        var running = service.RunAsync(
            ToSummary(session),
            AudioSourceKind.Microphone,
            _model,
            "es",
            cancellation.Token);
        await factory.Started.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);

        var cancelled = Assert.Single(await _store.ListModelRevisionsAsync(session.Id));
        Assert.Equal(ModelRevisionStatus.Cancelled, cancelled.Status);
        Assert.Null(cancelled.ModelHash);
        Assert.Empty(await _store.ListModelRevisionsAsync(session.Id, null, successfulOnly: true));
    }

    [Fact]
    public async Task RunAsync_TransportDisposeThrows_SuccessRemainsCommittedAndNextAttemptIsNotBlocked()
    {
        var session = await CreateSessionAsync(SessionState.Completed);
        await WriteAudioAsync(session, AudioSourceKind.Microphone, 1);
        var service = new HistoryRetranscriptionService(
            _store,
            _archive,
            new ThrowingDisposeTransportFactory());

        var first = await service.RunAsync(ToSummary(session), AudioSourceKind.Microphone, _model, "es");
        var second = await service.RunAsync(ToSummary(session), AudioSourceKind.Microphone, _model, "es");

        Assert.Equal(ModelRevisionStatus.Succeeded, first.Status);
        Assert.Equal(ModelRevisionStatus.Succeeded, second.Status);
        Assert.Equal(2, (await _store.ListModelRevisionsAsync(session.Id, AudioSourceKind.Microphone, successfulOnly: true)).Count);
    }
    [Fact]
    public async Task InitializeAsync_Twice_PreservesModelRevisionSchemaAndSourceSeparation()
    {
        var session = await CreateSessionAsync(SessionState.Completed);
        var revision = await _store.StartModelRevisionAsync(session.Id, AudioSourceKind.Microphone, "model", "hash", "es");
        await _store.FinishModelRevisionAsync(revision.Id, ModelRevisionStatus.Succeeded, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1), null);

        await _store.InitializeAsync();
        await _store.InitializeAsync();

        Assert.Single(await _store.ListModelRevisionsAsync(session.Id, AudioSourceKind.Microphone, true));
        Assert.Empty(await _store.ListModelRevisionsAsync(session.Id, AudioSourceKind.SystemOutput, true));
    }

    [Fact]
    public async Task RunAsync_ConcurrentAttemptRejectedAndCancellationNeverActivatesRevision()
    {
        var session = await CreateSessionAsync(SessionState.Completed);
        await WriteAudioAsync(session, AudioSourceKind.Microphone, 1);
        var factory = new BlockingTransportFactory();
        var service = new HistoryRetranscriptionService(_store, _archive, factory);
        using var cancellation = new CancellationTokenSource();
        var running = service.RunAsync(ToSummary(session), AudioSourceKind.Microphone, _model, "es", cancellation.Token);
        await factory.Started.Task;

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunAsync(ToSummary(session), AudioSourceKind.Microphone, _model, "es"));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);

        var cancelled = Assert.Single(await _store.ListModelRevisionsAsync(session.Id));
        Assert.Equal(ModelRevisionStatus.Cancelled, cancelled.Status);
        Assert.Empty(await _store.ListModelRevisionsAsync(session.Id, null, successfulOnly: true));
    }
    [Fact]
    public void RetainedAudioContinuity_LateInitialOrIntermediateSequence_FailsClosed()
    {
        var start = DateTimeOffset.UtcNow;
        var late = new ArchivedAudioChunk("late", "session", AudioSourceKind.Microphone, 1, start.AddSeconds(30), TimeSpan.FromSeconds(30), "late", 1);
        var first = new ArchivedAudioChunk("first", "session", AudioSourceKind.Microphone, 0, start, TimeSpan.FromSeconds(30), "first", 1);
        var gap = new ArchivedAudioChunk("gap", "session", AudioSourceKind.Microphone, 2, start.AddSeconds(60), TimeSpan.FromSeconds(30), "gap", 1);

        Assert.Throws<InvalidOperationException>(() => RetainedAudioContinuity.Validate([late], start));
        Assert.Throws<InvalidOperationException>(() => RetainedAudioContinuity.Validate([first, gap], start));
    }

    [Fact]
    public async Task RunAsync_SourceBeginsAfterSessionStart_PreservesRealTimelineInsteadOfRebasing()
    {
        var session = await CreateSessionAsync(SessionState.Completed);
        await WriteAudioAsync(session, AudioSourceKind.Microphone, 1, TimeSpan.FromSeconds(12));
        var service = new HistoryRetranscriptionService(_store, _archive, new FakeTransportFactory());

        var revision = await service.RunAsync(ToSummary(session), AudioSourceKind.Microphone, _model, "es");
        var segment = Assert.Single(await _store.GetModelRevisionSegmentsAsync(revision.Id));

        Assert.Equal(TimeSpan.FromSeconds(12), segment.Start);
    }

    [Fact]
    public async Task InitializeAsync_StaleRunningRevision_IsFailedWithEncryptedDiagnosticAndCanRestart()
    {
        var session = await CreateSessionAsync(SessionState.Completed);
        await _store.StartModelRevisionAsync(session.Id, AudioSourceKind.Microphone, "model", "verified", "es");

        var restarted = new SqliteSessionStore(Path.Combine(_root, "test.db"), _protector);
        await restarted.InitializeAsync();

        var failed = Assert.Single(await restarted.ListModelRevisionsAsync(session.Id));
        Assert.Equal(ModelRevisionStatus.Failed, failed.Status);
        Assert.Contains("aplicación se cerró", failed.Error, StringComparison.OrdinalIgnoreCase);
        await restarted.StartModelRevisionAsync(session.Id, AudioSourceKind.Microphone, "model", "verified", "es");
        var raw = System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(Path.Combine(_root, "test.db")));
        Assert.DoesNotContain("aplicación se cerró", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartModelRevision_TwoStoresConcurrently_AllowsOnlyOneRunningRevision()
    {
        var session = await CreateSessionAsync(SessionState.Completed);
        var secondStore = new SqliteSessionStore(Path.Combine(_root, "test.db"), _protector);
        await secondStore.InitializeAsync();
        var starts = new[]
        {
            _store.StartModelRevisionAsync(session.Id, AudioSourceKind.SystemOutput, "model-a", "hash-a", "es"),
            secondStore.StartModelRevisionAsync(session.Id, AudioSourceKind.SystemOutput, "model-b", "hash-b", "es")
        };
        var outcomes = await Task.WhenAll(starts.Select(async task =>
        {
            try { await task; return true; }
            catch (InvalidOperationException) { return false; }
        }));

        Assert.Equal(1, outcomes.Count(value => value));
        Assert.Single(await _store.ListModelRevisionsAsync(session.Id, AudioSourceKind.SystemOutput), item => item.Status == ModelRevisionStatus.Running);
    }
    private async Task<MeetingSession> CreateSessionAsync(SessionState state)
    {
        var meeting = new MeetingSession(Guid.NewGuid().ToString("N"), "Meeting", DateTimeOffset.UtcNow, state == SessionState.Recording ? null : DateTimeOffset.UtcNow, state);
        await _store.CreateSessionAsync(meeting);
        return meeting;
    }

    private async Task WriteAudioAsync(MeetingSession session, AudioSourceKind source, int seconds, TimeSpan? startOffset = null)
    {
        await using var writer = _archive.CreateSession(session.Id, long.MaxValue);
        await writer.AppendAsync(new(source, new byte[seconds * 32_000], session.StartedAt + (startOffset ?? TimeSpan.Zero)));
        await writer.CompleteAsync();
    }

    private static SessionSummary ToSummary(MeetingSession session) => new(session.Id, session.Title, session.StartedAt, session.EndedAt, session.State, session.LocalSpeakerName);

    private sealed class CountingProtector(IContentProtector inner) : IContentProtector
    {
        public int UnprotectCalls { get; private set; }
        public EncryptedPayload Protect(ReadOnlySpan<byte> plaintext, string associatedData) =>
            inner.Protect(plaintext, associatedData);
        public byte[] Unprotect(EncryptedPayload payload, string associatedData)
        {
            UnprotectCalls++;
            return inner.Unprotect(payload, associatedData);
        }
    }

    private sealed class IntervalTransportFactory : ITranscriptionTransportFactory
    {
        public byte[]? Pcm { get; private set; }
        public Task<ITranscriptionTransport> StartAsync(string modelPath, string language, CancellationToken cancellationToken) =>
            Task.FromResult<ITranscriptionTransport>(new IntervalTransport(this));

        private sealed class IntervalTransport(IntervalTransportFactory owner) : ITranscriptionTransport
        {
            public string? VerifiedModelHash => "FAKE-INTERVAL-HASH";
            public Task<WorkerResponse> TranscribeAsync(string workId, byte[] pcm16, CancellationToken cancellationToken)
            {
                owner.Pcm = pcm16.ToArray();
                return Task.FromResult(new WorkerResponse(true, WorkId: workId,
                    Segments: [new(0, 500, "segunda versión")]));
            }
            public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class ProductionPipeTimeoutTransportFactory : ITranscriptionTransportFactory
    {
        public async Task<ITranscriptionTransport> StartAsync(string modelPath, string language, CancellationToken cancellationToken)
        {
            await using var client = new WorkerClient($"trazio-missing-{Guid.NewGuid():N}");
            await client.ConnectAsync(TimeSpan.FromMilliseconds(100), cancellationToken);
            throw new UnreachableException();
        }
    }
    private sealed class ThrowingStartTransportFactory(Exception exception) : ITranscriptionTransportFactory
    {
        public Task<ITranscriptionTransport> StartAsync(string modelPath, string language, CancellationToken cancellationToken) =>
            Task.FromException<ITranscriptionTransport>(exception);
    }

    private sealed class CancellableStartTransportFactory : ITranscriptionTransportFactory
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ITranscriptionTransport> StartAsync(string modelPath, string language, CancellationToken cancellationToken)
        {
            Started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new UnreachableException();
        }
    }

    private sealed class ThrowingDisposeTransportFactory : ITranscriptionTransportFactory
    {
        public Task<ITranscriptionTransport> StartAsync(string modelPath, string language, CancellationToken cancellationToken) =>
            Task.FromResult<ITranscriptionTransport>(new ThrowingDisposeTransport());
    }

    private sealed class ThrowingDisposeTransport : ITranscriptionTransport
    {
        public string? VerifiedModelHash => "FAKE-VERIFIED-HASH";
        public Task<WorkerResponse> TranscribeAsync(string workId, byte[] pcm16, CancellationToken cancellationToken) =>
            Task.FromResult(new WorkerResponse(true, WorkId: workId, Segments: [new(0, 500, "generated")]));
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.FromException(new InvalidOperationException("dispose failure"));
    }
    private sealed class BlockingTransportFactory : ITranscriptionTransportFactory
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ITranscriptionTransport> StartAsync(string modelPath, string language, CancellationToken cancellationToken) => Task.FromResult<ITranscriptionTransport>(new BlockingTransport(Started));
    }

    private sealed class BlockingTransport(TaskCompletionSource started) : ITranscriptionTransport
    {
        public string? VerifiedModelHash { get; } = "FAKE-VERIFIED-HASH";
        public async Task<WorkerResponse> TranscribeAsync(string workId, byte[] pcm16, CancellationToken cancellationToken)
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new UnreachableException();
        }
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class FakeTransportFactory : ITranscriptionTransportFactory
    {
        public string? Error { get; init; }
        public FakeTransport? Transport { get; private set; }
        public Task<ITranscriptionTransport> StartAsync(string modelPath, string language, CancellationToken cancellationToken) => Task.FromResult<ITranscriptionTransport>(Transport = new(Error));
    }

    private sealed class FakeTransport(string? error) : ITranscriptionTransport
    {
        public string? VerifiedModelHash { get; } = "FAKE-VERIFIED-HASH";
        public List<long> Calls { get; } = [];
        public Task<WorkerResponse> TranscribeAsync(string workId, byte[] pcm16, CancellationToken cancellationToken)
        {
            var sequence = long.Parse(workId[(workId.LastIndexOf(':') + 1)..]);
            Calls.Add(sequence);
            return Task.FromResult(error is null
                ? new WorkerResponse(true, WorkId: workId, Segments: [new(0, 500, $"generated-{sequence}")])
                : new WorkerResponse(false, error, workId));
        }
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}





