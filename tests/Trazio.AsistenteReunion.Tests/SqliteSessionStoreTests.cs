using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Trazio.AsistenteReunion.App;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.Tests;

public sealed class SqliteSessionStoreTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "trazio-tests-" + Guid.NewGuid().ToString("N"));
    private AesContentProtector _protector = null!;
    private SqliteSessionStore _store = null!;
    private string DatabasePath => Path.Combine(_directory, "test.db");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        _protector = new(RandomNumberGenerator.GetBytes(32));
        _store = new(DatabasePath, _protector);
        await _store.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        _protector.Dispose();
        Directory.Delete(_directory, true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task SaveSegment_DuplicateWork_IsIdempotentAndReadable()
    {
        var session = NewSession();
        await _store.CreateSessionAsync(session);
        var segment = new TranscriptSegment("seg-1", session.Id, AudioSourceKind.Microphone, 1, TimeSpan.Zero, TimeSpan.FromSeconds(2), "secret words", DateTimeOffset.UtcNow);
        Assert.True(await _store.SaveSegmentAsync(segment));
        Assert.False(await _store.SaveSegmentAsync(segment));
        var result = Assert.Single(await _store.GetSegmentsAsync(session.Id));
        Assert.Equal("secret words", result.Text);
    }

    [Fact]
    public async Task UpdateSessionTitle_EncryptsAndReturnsRenamedSession()
    {
        var session = NewSession("Título original");
        await _store.CreateSessionAsync(session);

        Assert.True(await _store.UpdateSessionTitleAsync(session.Id, "  Reunión de planificación  "));

        var renamed = Assert.Single(await _store.ListSessionsAsync());
        var raw = System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(DatabasePath));
        Assert.Equal("Reunión de planificación", renamed.Title);
        Assert.DoesNotContain("Reunión de planificación", raw);
    }

    [Fact]
    public async Task DeleteSession_CascadesSegmentsAndPendingAudio()
    {
        var session = NewSession();
        await _store.CreateSessionAsync(session);
        await _store.SaveSegmentAsync(new("seg", session.Id, AudioSourceKind.Microphone, 1, TimeSpan.Zero, TimeSpan.FromSeconds(1), "text", DateTimeOffset.UtcNow));
        await _store.SavePendingAsync(AudioChunk.Create(session.Id, AudioSourceKind.Microphone, 1, DateTimeOffset.UtcNow, [1, 2]), DateTimeOffset.UtcNow.AddHours(1));
        await _store.DeleteSessionAsync(session.Id);
        Assert.Empty(await _store.GetSegmentsAsync(session.Id));
        Assert.Equal(0, await ScalarCountAsync("pending_audio"));
    }

    [Fact]
    public async Task DeleteExpiredPending_RemovesOnlyExpiredRows()
    {
        var session = NewSession(state: SessionState.Completed);
        await _store.CreateSessionAsync(session);
        var now = DateTimeOffset.UtcNow;
        await _store.SavePendingAsync(AudioChunk.Create(session.Id, AudioSourceKind.Microphone, 1, now, [1]), now.AddMinutes(-1));
        await _store.SavePendingAsync(AudioChunk.Create(session.Id, AudioSourceKind.Microphone, 2, now, [2]), now.AddMinutes(1));
        Assert.Equal(1, await _store.DeleteExpiredPendingAsync(now));
        Assert.Equal(1, await ScalarCountAsync("pending_audio"));
    }

    [Fact]
    public async Task DeleteExpiredPending_RemovesExpiredInterruptedSessionAudio()
    {
        var session = NewSession(state: SessionState.Interrupted);
        await _store.CreateSessionAsync(session);
        var now = DateTimeOffset.UtcNow;
        await _store.SavePendingAsync(AudioChunk.Create(session.Id, AudioSourceKind.Microphone, 1, now, [1]), now.AddDays(-1));
        Assert.Equal(1, await _store.DeleteExpiredPendingAsync(now));
        Assert.Empty(await _store.GetPendingAsync(session.Id));
    }

    [Fact]
    public async Task DeleteExpiredPending_PreservesRecentInterruptedSessionAudio()
    {
        var session = NewSession(state: SessionState.Interrupted);
        await _store.CreateSessionAsync(session);
        var now = DateTimeOffset.UtcNow;
        await _store.SavePendingAsync(AudioChunk.Create(session.Id, AudioSourceKind.Microphone, 1, now, [1]), now.AddMinutes(1));
        Assert.Equal(0, await _store.DeleteExpiredPendingAsync(now));
        Assert.Single(await _store.GetPendingAsync(session.Id));
    }

    [Fact]
    public async Task DeleteExpiredPending_PreservesActiveRecordingSessionAudio()
    {
        var session = NewSession(state: SessionState.Recording);
        await _store.CreateSessionAsync(session);
        var now = DateTimeOffset.UtcNow;
        await _store.SavePendingAsync(AudioChunk.Create(session.Id, AudioSourceKind.Microphone, 1, now, [1]), now.AddDays(-1));
        Assert.Equal(0, await _store.DeleteExpiredPendingAsync(now));
        Assert.Single(await _store.GetPendingAsync(session.Id));
    }

    [Fact]
    public async Task GetPending_DecryptsRestartSafeAudio()
    {
        var session = NewSession();
        await _store.CreateSessionAsync(session);
        var chunk = AudioChunk.Create(session.Id, AudioSourceKind.SystemOutput, 7, DateTimeOffset.UtcNow, [4, 5, 6]);
        await _store.SavePendingAsync(chunk, DateTimeOffset.UtcNow.AddHours(24));
        var restored = Assert.Single(await _store.GetPendingAsync(session.Id));
        Assert.Equal(chunk.Id, restored.Id);
        Assert.Equal(chunk.Pcm16, restored.Pcm16);
    }

    [Fact]
    public async Task Database_DoesNotContainPlaintextSensitiveValues()
    {
        var session = NewSession("Highly confidential title");
        await _store.CreateSessionAsync(session);
        await _store.SaveSegmentAsync(new("seg", session.Id, AudioSourceKind.Microphone, 1, TimeSpan.Zero, TimeSpan.FromSeconds(1), "secret transcript phrase", DateTimeOffset.UtcNow));
        var bytes = await File.ReadAllBytesAsync(DatabasePath);
        var raw = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain("Highly confidential title", raw);
        Assert.DoesNotContain("secret transcript phrase", raw);
    }

    [Fact]
    public async Task SaveSegment_MicrophoneSpeaker_IsEncryptedAndReadable()
    {
        var session = NewSession() with { LocalSpeakerName = "Andrea Private" };
        await _store.CreateSessionAsync(session);
        await _store.SaveSegmentAsync(new("speaker-seg", session.Id, AudioSourceKind.Microphone, 1,
            TimeSpan.Zero, TimeSpan.FromSeconds(1), "hello", DateTimeOffset.UtcNow, "Andrea Private"));

        var listedSession = Assert.Single(await _store.ListSessionsAsync());
        var segment = Assert.Single(await _store.GetSegmentsAsync(session.Id));
        var raw = System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(DatabasePath));

        Assert.Equal("Andrea Private", listedSession.LocalSpeakerName);
        Assert.Equal("Andrea Private", segment.SpeakerName);
        Assert.DoesNotContain("Andrea Private", raw);
    }

    [Fact]
    public async Task SaveSegment_SystemOutput_DoesNotAttributeLocalSpeaker()
    {
        var session = NewSession();
        await _store.CreateSessionAsync(session);
        await _store.SaveSegmentAsync(new("system-seg", session.Id, AudioSourceKind.SystemOutput, 1,
            TimeSpan.Zero, TimeSpan.FromSeconds(1), "remote voice", DateTimeOffset.UtcNow, "Must not persist"));

        var segment = Assert.Single(await _store.GetSegmentsAsync(session.Id));

        Assert.Null(segment.SpeakerName);
    }

    [Fact]
    public async Task RecordsWithoutSpeaker_RemainReadable()
    {
        var session = NewSession();
        await _store.CreateSessionAsync(session);
        await _store.SaveSegmentAsync(new("old-seg", session.Id, AudioSourceKind.Microphone, 1,
            TimeSpan.Zero, TimeSpan.FromSeconds(1), "legacy text", DateTimeOffset.UtcNow));

        var listedSession = Assert.Single(await _store.ListSessionsAsync());
        var segment = Assert.Single(await _store.GetSegmentsAsync(session.Id));

        Assert.Null(listedSession.LocalSpeakerName);
        Assert.Null(segment.SpeakerName);
    }
    [Fact]
    public async Task MeetingProvider_RoundTripsWithoutPersistingTransientWindowDetails()
    {
        const string transientWindowTitle = "PRIVATE Meet title that must never reach SQLite";
        var candidate = new MeetingWindowCandidate((nint)123, 456, transientWindowTitle, "chrome", MeetingProvider.GoogleMeet, false);
        var session = NewSession("Stored session title") with { MeetingProvider = candidate.Provider };
        await _store.CreateSessionAsync(session);
        var restored = Assert.Single(await _store.ListSessionsAsync());
        var raw = System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(DatabasePath));
        Assert.Equal(MeetingProvider.GoogleMeet, restored.MeetingProvider);
        Assert.DoesNotContain(transientWindowTitle, raw, StringComparison.Ordinal);
    }
    [Fact]
    public async Task InitializeAsync_OldSchema_AddsSpeakerColumnsWithoutLosingCompatibility()
    {
        var oldDatabasePath = Path.Combine(_directory, "old-schema.db");
        await using (var connection = new SqliteConnection($"Data Source={oldDatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE sessions (
                  id TEXT PRIMARY KEY, title_nonce BLOB NOT NULL, title_cipher BLOB NOT NULL, title_tag BLOB NOT NULL,
                  started_at TEXT NOT NULL, ended_at TEXT NULL, state INTEGER NOT NULL);
                CREATE TABLE segments (
                  id TEXT PRIMARY KEY, session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
                  source INTEGER NOT NULL, sequence INTEGER NOT NULL, start_ms INTEGER NOT NULL, end_ms INTEGER NOT NULL,
                  text_nonce BLOB NOT NULL, text_cipher BLOB NOT NULL, text_tag BLOB NOT NULL, created_at TEXT NOT NULL,
                  UNIQUE(id));
                """;
            await command.ExecuteNonQueryAsync();
        }

        var migratedStore = new SqliteSessionStore(oldDatabasePath, _protector);
        await migratedStore.InitializeAsync();
        var session = new MeetingSession("migrated", "Migrated", DateTimeOffset.UtcNow, null, SessionState.Recording, "Andrea");
        await migratedStore.CreateSessionAsync(session);
        await migratedStore.SaveSegmentAsync(new("migrated-seg", session.Id, AudioSourceKind.Microphone, 1,
            TimeSpan.Zero, TimeSpan.FromSeconds(1), "text", DateTimeOffset.UtcNow, "Andrea"));

        var restored = Assert.Single(await migratedStore.ListSessionsAsync());
        Assert.Equal("Andrea", restored.LocalSpeakerName);
        Assert.Equal(MeetingProvider.NotSelected, restored.MeetingProvider);
        Assert.Equal("Andrea", Assert.Single(await migratedStore.GetSegmentsAsync(session.Id)).SpeakerName);
    }
    private MeetingSession NewSession(string title = "Meeting", SessionState state = SessionState.Recording) => new(Guid.NewGuid().ToString("N"), title, DateTimeOffset.UtcNow, null, state);

    private async Task<long> ScalarCountAsync(string table)
    {
        await using var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False");
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table}";
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
