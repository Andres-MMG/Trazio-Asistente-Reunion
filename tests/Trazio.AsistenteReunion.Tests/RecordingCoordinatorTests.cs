using System.Security.Cryptography;
using Trazio.AsistenteReunion.App;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.Tests;

public sealed class RecordingCoordinatorTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "trazio-coordinator-" + Guid.NewGuid().ToString("N"));
    private readonly string _modelPath;
    private AesContentProtector _protector = null!;
    private SqliteSessionStore _store = null!;

    public RecordingCoordinatorTests() => _modelPath = Path.Combine(_directory, "model.bin");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllBytesAsync(_modelPath, [1]);
        _protector = new(RandomNumberGenerator.GetBytes(32));
        _store = new(Path.Combine(_directory, "test.db"), _protector);
        await _store.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        _protector.Dispose();
        Directory.Delete(_directory, true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task StopAsync_WithAcceptedTail_DrainsPersistsAndCompletesSession()
    {
        var capture = new FakeAudioCapture();
        var factory = new FakeTransportFactory();
        await using var coordinator = new RecordingCoordinator(capture, _store, new PendingAudioQueue(), factory);
        await coordinator.StartAsync("Tail drain", Settings());
        capture.Emit(AudioSourceKind.Microphone, [1, 2, 3, 4]);
        capture.Emit(AudioSourceKind.Microphone, [5, 6, 7, 8]);

        await coordinator.StopAsync();

        var session = Assert.Single(await _store.ListSessionsAsync());
        Assert.Equal(SessionState.Completed, session.State);
        Assert.Single(await _store.GetSegmentsAsync(session.Id));
        Assert.Empty(await _store.GetPendingAsync(session.Id));
        Assert.Equal(1, factory.Transports.Single().TranscriptionCount);
    }

    [Fact]
    public async Task StopAsync_DuringDequeueHandoff_WaitsForLeasedChunk()
    {
        var handoffReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHandoff = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new PendingAudioQueue(afterLeaseTransfer: async cancellationToken =>
        {
            handoffReached.TrySetResult();
            await releaseHandoff.Task.WaitAsync(cancellationToken);
        });
        var capture = new FakeAudioCapture();
        await using var coordinator = new RecordingCoordinator(capture, _store, queue, new FakeTransportFactory());
        await coordinator.StartAsync("Lease handoff", Settings());
        capture.Emit(AudioSourceKind.Microphone, [1, 2, 3, 4]);
        await handoffReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, queue.LeasedCount);
        Assert.Equal(1, queue.OutstandingCount);

        var stopTask = coordinator.StopAsync();
        await Task.Delay(100);
        Assert.False(stopTask.IsCompleted);

        releaseHandoff.TrySetResult();
        await stopTask.WaitAsync(TimeSpan.FromSeconds(5));
        var session = Assert.Single(await _store.ListSessionsAsync());
        Assert.Equal(SessionState.Completed, session.State);
        Assert.Empty(await _store.GetPendingAsync(session.Id));
        Assert.Equal(0, queue.OutstandingCount);
    }

    [Fact]
    public async Task StartAsync_WhenCaptureStartupFails_RollsBackAndCanRetry()
    {
        var capture = new FakeAudioCapture { StartFailuresRemaining = 1 };
        var factory = new FakeTransportFactory();
        await using var coordinator = new RecordingCoordinator(capture, _store, new PendingAudioQueue(), factory);

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.StartAsync("Failed", Settings()));
        Assert.Empty(await _store.ListSessionsAsync());

        await coordinator.StartAsync("Retry", Settings());
        capture.Emit(AudioSourceKind.Microphone, [1, 2]);
        await coordinator.StopAsync();

        var session = Assert.Single(await _store.ListSessionsAsync());
        Assert.Equal("Retry", session.Title);
        Assert.Equal(SessionState.Completed, session.State);
        Assert.Equal(2, capture.StartCount);
        Assert.True(capture.StopCount >= 2);
    }

    [Fact]
    public async Task StartAsync_WhenWorkerStartupFails_DeletesEmptySessionAndCanRetry()
    {
        var capture = new FakeAudioCapture();
        var factory = new FakeTransportFactory { StartFailuresRemaining = 1 };
        await using var coordinator = new RecordingCoordinator(capture, _store, new PendingAudioQueue(), factory);

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.StartAsync("Failed", Settings()));
        Assert.Empty(await _store.ListSessionsAsync());

        await coordinator.StartAsync("Retry worker", Settings());
        capture.Emit(AudioSourceKind.Microphone, [1, 2]);
        await coordinator.StopAsync();
        Assert.Equal(SessionState.Completed, Assert.Single(await _store.ListSessionsAsync()).State);
    }

    [Fact]
    public async Task StopAsync_WhenAudioOptedIn_FlushesEncryptedTailBySource()
    {
        var capture = new FakeAudioCapture();
        var archive = new AudioArchiveStore(Path.Combine(_directory, "audio"), _store, _protector);
        await using var coordinator = new RecordingCoordinator(capture, _store, new PendingAudioQueue(), new FakeTransportFactory(), archive);
        await coordinator.StartAsync("Archived", Settings() with { KeepEncryptedAudio = true });
        capture.Emit(AudioSourceKind.Microphone, new byte[32_000]);
        await coordinator.StopAsync();

        var session = Assert.Single(await _store.ListSessionsAsync());
        var chunk = Assert.Single(await _store.GetArchivedAudioAsync(session.Id));
        Assert.Equal(AudioSourceKind.Microphone, chunk.Source);
        Assert.Equal(TimeSpan.FromSeconds(1), chunk.Duration);
    }

    [Fact]
    public async Task StartAsync_OldSessionCallback_IsRejectedByRevision()
    {
        var capture = new FakeAudioCapture();
        await using var coordinator = new RecordingCoordinator(capture, _store, new PendingAudioQueue(), new FakeTransportFactory());
        await coordinator.StartAsync("First", Settings());
        var stale = capture.SnapshotHandler();
        var staleFailure = capture.SnapshotFailureHandler();
        await coordinator.StopAsync();
        await coordinator.StartAsync("Second", Settings());
        stale(capture, new(AudioSourceKind.Microphone, [1, 2], 0.4f));
        staleFailure(capture, "old device failure");
        capture.Emit(AudioSourceKind.Microphone, [3, 4]);
        await coordinator.StopAsync();

        var second = (await _store.ListSessionsAsync()).Single(s => s.Title == "Second");
        Assert.Single(await _store.GetSegmentsAsync(second.Id));
    }

    [Fact]
    public async Task StopAsync_AfterConsumerFailure_CancelsAndAwaitsDiagnosticPump()
    {
        var capture = new FakeAudioCapture();
        var factory = new FakeTransportFactory { ThrowOnTranscribe = true };
        await using var coordinator = new RecordingCoordinator(capture, _store, new PendingAudioQueue(), factory);
        var diagnostics = 0;
        coordinator.DiagnosticChanged += (_, _) => Interlocked.Increment(ref diagnostics);
        await coordinator.StartAsync("Failure", Settings());
        capture.Emit(AudioSourceKind.Microphone, [1, 2]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.StopAsync());
        var afterStop = Volatile.Read(ref diagnostics);
        await Task.Delay(2200);
        Assert.Equal(afterStop, Volatile.Read(ref diagnostics));
    }

    [Fact]
    public async Task StartAsync_WithMeetingOverride_AttributesOnlyMicrophoneSegments()
    {
        var capture = new FakeAudioCapture();
        await using var coordinator = new RecordingCoordinator(capture, _store, new PendingAudioQueue(), new FakeTransportFactory());
        var settings = Settings() with { CaptureSystemOutput = true };
        await coordinator.StartAsync("Attributed", settings, localDisplayNameOverride: "Meeting Guest");
        capture.Emit(AudioSourceKind.Microphone, [1, 2]);
        capture.Emit(AudioSourceKind.SystemOutput, [3, 4]);

        await coordinator.StopAsync();

        var session = Assert.Single(await _store.ListSessionsAsync());
        var segments = await _store.GetSegmentsAsync(session.Id);
        Assert.Equal("Meeting Guest", session.LocalSpeakerName);
        Assert.Equal("Meeting Guest", Assert.Single(segments, item => item.Source == AudioSourceKind.Microphone).SpeakerName);
        Assert.Null(Assert.Single(segments, item => item.Source == AudioSourceKind.SystemOutput).SpeakerName);
        Assert.Equal("Local Profile", settings.LocalDisplayName);
    }
    [Fact]
    public async Task ConsecutiveMeetings_OverrideDoesNotLeakIntoNextMeeting()
    {
        var capture = new FakeAudioCapture();
        await using var coordinator = new RecordingCoordinator(capture, _store, new PendingAudioQueue(), new FakeTransportFactory());
        await coordinator.StartAsync("First", Settings(), localDisplayNameOverride: "First Guest");
        capture.Emit(AudioSourceKind.Microphone, [1, 2]);
        await coordinator.StopAsync();

        await coordinator.StartAsync("Second", Settings());
        capture.Emit(AudioSourceKind.Microphone, [3, 4]);
        await coordinator.StopAsync();

        var sessions = await _store.ListSessionsAsync();
        var first = sessions.Single(item => item.Title == "First");
        var second = sessions.Single(item => item.Title == "Second");
        Assert.Equal("First Guest", Assert.Single(await _store.GetSegmentsAsync(first.Id)).SpeakerName);
        Assert.Equal("Local Profile", Assert.Single(await _store.GetSegmentsAsync(second.Id)).SpeakerName);
    }
    private AppSettings Settings() => new("mic", null, _modelPath, "es", true, false,
        LocalDisplayName: "Local Profile", LocalProfileConfirmed: true);

    private sealed class FakeAudioCapture : IAudioCaptureService
    {
        public int StartFailuresRemaining { get; set; }
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        private EventHandler<CapturedSecond>? _secondCaptured;
        private EventHandler<string>? _captureFailed;
        public event EventHandler<CapturedSecond>? SecondCaptured { add => _secondCaptured += value; remove => _secondCaptured -= value; }
        public event EventHandler<string>? CaptureFailed { add => _captureFailed += value; remove => _captureFailed -= value; }

        public void Start(string? microphoneId, string? outputId, bool useMicrophone, bool useOutput)
        {
            StartCount++;
            if (StartFailuresRemaining-- > 0) throw new InvalidOperationException("Simulated capture startup failure.");
        }

        public void Emit(AudioSourceKind source, byte[] pcm) => _secondCaptured?.Invoke(this, new(source, pcm, 0.5f));
        public EventHandler<CapturedSecond> SnapshotHandler() => _secondCaptured ?? throw new InvalidOperationException("No capture handler is registered.");
        public EventHandler<string> SnapshotFailureHandler() => _captureFailed ?? throw new InvalidOperationException("No failure handler is registered.");
        public void Fail(string error) => _captureFailed?.Invoke(this, error);
        public Task StopAsync() { StopCount++; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeTransportFactory : ITranscriptionTransportFactory
    {
        public int StartFailuresRemaining { get; set; }
        public bool ThrowOnTranscribe { get; set; }
        public List<FakeTransport> Transports { get; } = [];

        public Task<ITranscriptionTransport> StartAsync(string modelPath, string language, CancellationToken cancellationToken)
        {
            if (StartFailuresRemaining-- > 0) throw new InvalidOperationException("Simulated worker startup failure.");
            var transport = new FakeTransport { ThrowOnTranscribe = ThrowOnTranscribe };
            Transports.Add(transport);
            return Task.FromResult<ITranscriptionTransport>(transport);
        }
    }

    private sealed class FakeTransport : ITranscriptionTransport
    {
        public string? VerifiedModelHash { get; } = "FAKE-VERIFIED-HASH";
        public int TranscriptionCount { get; private set; }
        public bool Disposed { get; private set; }
        public bool ThrowOnTranscribe { get; init; }

        public Task<WorkerResponse> TranscribeAsync(string workId, byte[] pcm16, CancellationToken cancellationToken)
        {
            TranscriptionCount++;
            if (ThrowOnTranscribe) throw new InvalidOperationException("Simulated inference failure.");
            IReadOnlyList<WorkerSegmentDto> segments = [new(0, 900, "tail text")];
            return Task.FromResult(new WorkerResponse(true, WorkId: workId, Segments: segments));
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}

