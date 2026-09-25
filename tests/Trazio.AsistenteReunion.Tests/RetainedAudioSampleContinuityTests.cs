using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Trazio.AsistenteReunion.App;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.Tests;

public sealed class RetainedAudioSampleContinuityTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "trazio-continuity-" + Guid.NewGuid().ToString("N"));
    private AesContentProtector _protector = null!;
    private SqliteSessionStore _store = null!;
    private AudioArchiveStore _archive = null!;
    private string _databasePath = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _databasePath = Path.Combine(_root, "test.db");
        _protector = new(RandomNumberGenerator.GetBytes(32));
        _store = new(_databasePath, _protector);
        await _store.InitializeAsync();
        _archive = new(Path.Combine(_root, "audio"), _store, _protector);
    }

    public Task DisposeAsync()
    {
        _protector.Dispose();
        Directory.Delete(_root, true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task InitializeAsync_AddsNullableContinuityColumnsToLegacyArchiveTable()
    {
        var legacyPath = Path.Combine(_root, "legacy.db");
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = legacyPath, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE archived_audio (
                  id TEXT PRIMARY KEY, session_id TEXT NOT NULL, source INTEGER NOT NULL,
                  sequence INTEGER NOT NULL, started_at TEXT NOT NULL, duration_ms INTEGER NOT NULL,
                  relative_path TEXT NOT NULL UNIQUE, encrypted_bytes INTEGER NOT NULL,
                  UNIQUE(session_id, source, sequence));
                """;
            await command.ExecuteNonQueryAsync();
        }
        var migrated = new SqliteSessionStore(legacyPath, _protector);
        await migrated.InitializeAsync();
        await migrated.InitializeAsync();
        await using var reopened = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = legacyPath, Pooling = false }.ToString());
        await reopened.OpenAsync();
        await using var inspect = reopened.CreateCommand();
        inspect.CommandText = "PRAGMA table_info(archived_audio)";
        await using var reader = await inspect.ExecuteReaderAsync();
        var columns = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync()) columns.Add(reader.GetString(1));
        Assert.Contains("capture_run_id", columns);
        Assert.Contains("continuity_epoch", columns);
        Assert.Contains("first_source_sample", columns);
    }

    [Fact]
    public async Task ContinuousSamples_CrossThirtySecondBoundaryDespiteCallbackJitter()
    {
        var session = await CreateSessionAsync();
        await using (var writer = _archive.CreateSession(session.Id, long.MaxValue))
        {
            for (var second = 0; second < 31; second++)
            {
                var jitter = second == 0 ? 0 : second % 2 == 0 ? 8 : -6;
                await writer.AppendAsync(new(AudioSourceKind.Microphone,
                    Enumerable.Repeat((byte)second, 32_000).ToArray(),
                    session.StartedAt.AddSeconds(second).AddMilliseconds(jitter),
                    "run-a", 0, second * 16_000L));
            }
            await writer.CompleteAsync();
        }
        var chunks = await _store.GetArchivedAudioAsync(session.Id, AudioSourceKind.Microphone);
        Assert.Equal(2, chunks.Count);
        Assert.Equal(session.StartedAt.AddSeconds(30), chunks[1].StartedAt);
        Assert.Equal(480_000, chunks[1].FirstSourceSample);
        Assert.Equal("run-a", chunks[1].CaptureRunId);
        Assert.Equal(0, chunks[1].ContinuityEpoch);

        var audio = await ReadAsync(session, chunks, 29, 31);
        Assert.Equal(64_000, audio.Length);
        Assert.All(audio.AsSpan(0, 32_000).ToArray(), value => Assert.Equal((byte)29, value));
        Assert.All(audio.AsSpan(32_000).ToArray(), value => Assert.Equal((byte)30, value));
    }

    [Fact]
    public async Task ArchiveRejectsSixtySecondGapWithoutProducerEpochChange()
    {
        var session = await CreateSessionAsync();
        await using var writer = _archive.CreateSession(session.Id, long.MaxValue);
        await writer.AppendAsync(new(AudioSourceKind.SystemOutput, new byte[32_000],
            session.StartedAt, "run-a", 0, 0));
        await Assert.ThrowsAsync<InvalidDataException>(() => writer.AppendAsync(new(
            AudioSourceKind.SystemOutput, new byte[32_000],
            session.StartedAt.AddSeconds(61), "run-a", 0, 16_000)));
        await writer.CompleteAsync();
        var chunk = Assert.Single(await _store.GetArchivedAudioAsync(session.Id, AudioSourceKind.SystemOutput));
        Assert.Equal(TimeSpan.FromSeconds(1), chunk.Duration);
    }

    [Fact]
    public async Task ArchiveRejectsThreeHundredMillisecondUnmarkedGapBetweenHundredMillisecondPackets()
    {
        var session = await CreateSessionAsync();
        await using var writer = _archive.CreateSession(session.Id, long.MaxValue);
        await writer.AppendAsync(new(AudioSourceKind.SystemOutput, new byte[3_200],
            session.StartedAt, "run-a", 0, 0));
        await Assert.ThrowsAsync<InvalidDataException>(() => writer.AppendAsync(new(
            AudioSourceKind.SystemOutput, new byte[3_200],
            session.StartedAt.AddMilliseconds(400), "run-a", 0, 1_600)));
        await writer.CompleteAsync();
        var chunk = Assert.Single(await _store.GetArchivedAudioAsync(session.Id, AudioSourceKind.SystemOutput));
        Assert.Equal(TimeSpan.FromMilliseconds(100), chunk.Duration);
    }

    [Fact]
    public async Task PauseEpoch_FlushesPartialChunkAndRejectsCrossingEvenWhenTimesAppearContiguous()
    {
        var session = await CreateSessionAsync();
        await using (var writer = _archive.CreateSession(session.Id, long.MaxValue))
        {
            await writer.AppendAsync(new(AudioSourceKind.Microphone, new byte[10 * 32_000], session.StartedAt, "run-a", 0, 0));
            await writer.AppendAsync(new(AudioSourceKind.Microphone, new byte[10 * 32_000], session.StartedAt.AddSeconds(10), "run-a", 2, 160_000));
            await writer.CompleteAsync();
        }
        var chunks = await _store.GetArchivedAudioAsync(session.Id, AudioSourceKind.Microphone);
        Assert.Equal(2, chunks.Count);
        Assert.Equal(TimeSpan.FromSeconds(10), chunks[0].Duration);
        Assert.Equal(2, chunks[1].ContinuityEpoch);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAsync(session, chunks, 9, 11));
    }

    [Fact]
    public async Task DroppedCaptureSamples_FlushesAndRejectsApparentlyContiguousChunks()
    {
        var session = await CreateSessionAsync();
        await using (var writer = _archive.CreateSession(session.Id, long.MaxValue))
        {
            await writer.AppendAsync(new(AudioSourceKind.Microphone, new byte[10 * 32_000], session.StartedAt, "run-a", 0, 0));
            await writer.AppendAsync(new(AudioSourceKind.Microphone, new byte[10 * 32_000], session.StartedAt.AddSeconds(10), "run-a", 0, 176_000));
            await writer.CompleteAsync();
        }
        var chunks = await _store.GetArchivedAudioAsync(session.Id, AudioSourceKind.Microphone);
        Assert.Equal(2, chunks.Count);
        Assert.Equal(176_000, chunks[1].FirstSourceSample);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAsync(session, chunks, 9, 11));
    }

    [Fact]
    public async Task LegacyAudio_RemainsReadableWithinChunkButJitteredBoundaryFailsClosed()
    {
        var session = await CreateSessionAsync();
        await using (var writer = _archive.CreateSession(session.Id, long.MaxValue))
        {
            await writer.AppendAsync(new(AudioSourceKind.Microphone, new byte[30 * 32_000], session.StartedAt));
            await writer.AppendAsync(new(AudioSourceKind.Microphone, new byte[32_000], session.StartedAt.AddSeconds(30).AddMilliseconds(8)));
            await writer.CompleteAsync();
        }
        var chunks = await _store.GetArchivedAudioAsync(session.Id, AudioSourceKind.Microphone);
        Assert.All(chunks, chunk => Assert.Null(chunk.CaptureRunId));
        Assert.Equal(32_000, (await ReadAsync(session, chunks, 28, 29)).Length);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAsync(session, chunks, 29, 31));
    }

    [Fact]
    public async Task MixedLegacyAndSampleMetadata_RejectsCrossing()
    {
        var session = await CreateSessionAsync();
        await using (var writer = _archive.CreateSession(session.Id, long.MaxValue))
        {
            await writer.AppendAsync(new(AudioSourceKind.Microphone, new byte[32_000], session.StartedAt));
            await writer.AppendAsync(new(AudioSourceKind.Microphone, new byte[32_000], session.StartedAt.AddSeconds(1), "run-a", 0, 16_000));
            await writer.CompleteAsync();
        }
        var chunks = await _store.GetArchivedAudioAsync(session.Id, AudioSourceKind.Microphone);
        Assert.Equal(2, chunks.Count);
        Assert.Equal(32_000, (await ReadAsync(session, chunks, 0, 1)).Length);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAsync(session, chunks, 0, 2));
    }

    [Fact]
    public async Task CorruptMetadata_IsRejectedBeforeAudioIsUsed()
    {
        var session = await CreateSessionAsync();
        await using (var writer = _archive.CreateSession(session.Id, long.MaxValue))
        {
            await writer.AppendAsync(new(AudioSourceKind.Microphone, new byte[32_000], session.StartedAt, "run-a", 0, 0));
            await writer.CompleteAsync();
        }
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _databasePath, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE archived_audio SET first_source_sample=-1 WHERE session_id=$session";
            command.Parameters.AddWithValue("$session", session.Id);
            await command.ExecuteNonQueryAsync();
        }
        var chunks = await _store.GetArchivedAudioAsync(session.Id, AudioSourceKind.Microphone);
        await Assert.ThrowsAsync<InvalidDataException>(() => ReadAsync(session, chunks, 0, 1));
        var original = new TranscriptSegment("corrupt-original", session.Id, AudioSourceKind.Microphone,
            0, TimeSpan.Zero, TimeSpan.FromSeconds(1), "intacto", DateTimeOffset.UtcNow);
        await _store.SaveSegmentAsync(original);
        var factory = new NeverStartTransportFactory();
        var service = new HistoryRetranscriptionService(_store, _archive, factory);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.RunSelectedSegmentAsync(
            new SessionSummary(session.Id, session.Title, session.StartedAt, null, SessionState.Completed),
            original, Path.Combine(_root, "unused-model.bin"), "es"));
        Assert.False(factory.WasStarted);
        Assert.Empty(await _store.ListModelRevisionsAsync(session.Id));
        Assert.Equal("intacto", Assert.Single(await _store.GetSegmentsAsync(session.Id)).Text);
    }

    private sealed class NeverStartTransportFactory : ITranscriptionTransportFactory
    {
        public bool WasStarted { get; private set; }
        public Task<ITranscriptionTransport> StartAsync(string modelPath, string language, CancellationToken cancellationToken)
        {
            WasStarted = true;
            throw new InvalidOperationException("El transporte no debe arrancar con metadatos corruptos.");
        }
    }

    private async Task<MeetingSession> CreateSessionAsync()
    {
        var session = new MeetingSession(Guid.NewGuid().ToString("N"), "test", DateTimeOffset.UtcNow, null, SessionState.Recording);
        await _store.CreateSessionAsync(session);
        return session;
    }

    private Task<byte[]> ReadAsync(MeetingSession session, IReadOnlyList<ArchivedAudioChunk> chunks, int start, int end) =>
        RetainedAudioInterval.ReadExactAsync(_archive,
            new SessionSummary(session.Id, session.Title, session.StartedAt, null, SessionState.Completed),
            AudioSourceKind.Microphone, chunks, TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), CancellationToken.None);
}
