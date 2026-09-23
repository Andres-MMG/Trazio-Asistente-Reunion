using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.Tests;

public sealed class SqliteVisualEvidenceStoreTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "trazio-visual-tests-" + Guid.NewGuid().ToString("N"));
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
    public async Task SaveAnonymousVisualEvidence_DuplicateId_IsIdempotentAndRoundTrips()
    {
        var session = NewSession();
        await _store.CreateSessionAsync(session);
        var evidence = Activity(session.Id, Guid.ParseExact("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "N"));

        Assert.True(await _store.SaveAnonymousVisualEvidenceAsync(evidence));
        Assert.False(await _store.SaveAnonymousVisualEvidenceAsync(evidence));

        var restored = await _store.GetAnonymousVisualEvidenceAsync(session.Id);
        Assert.Equal(AnonymousVisualEvidenceReadStatus.Loaded, restored.Status);
        Assert.Equal(evidence, Assert.Single(restored.Intervals));
    }

    [Fact]
    public async Task SaveAnonymousVisualEvidence_DuplicateIdWithConflictingPayload_PreservesOriginal()
    {
        var session = NewSession();
        await _store.CreateSessionAsync(session);
        var id = Guid.ParseExact("dddddddddddddddddddddddddddddddd", "N");
        var original = Activity(session.Id, id, 1, 2, 0.95);
        var conflicting = Coverage(session.Id, id, 7, 9);

        Assert.True(await _store.SaveAnonymousVisualEvidenceAsync(original));
        Assert.False(await _store.SaveAnonymousVisualEvidenceAsync(conflicting));

        var restored = await _store.GetAnonymousVisualEvidenceAsync(session.Id);
        Assert.Equal(original, Assert.Single(restored.Intervals));
    }

    [Fact]
    public async Task GetAnonymousVisualEvidence_OrdersDecryptedIntervalsDeterministically()
    {
        var session = NewSession();
        await _store.CreateSessionAsync(session);
        var later = Activity(session.Id, Guid.ParseExact("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "N"), 8, 9);
        var earlier = Coverage(session.Id, Guid.ParseExact("cccccccccccccccccccccccccccccccc", "N"), 0, 5);

        await _store.SaveAnonymousVisualEvidenceAsync(later);
        await _store.SaveAnonymousVisualEvidenceAsync(earlier);

        var restored = await _store.GetAnonymousVisualEvidenceAsync(session.Id);
        Assert.Equal([earlier, later], restored.Intervals);
    }

    [Fact]
    public async Task Database_DoesNotContainPlaintextVisualPayload()
    {
        var session = NewSession();
        await _store.CreateSessionAsync(session);
        await _store.SaveAnonymousVisualEvidenceAsync(Activity(session.Id, Guid.NewGuid(), 123, 456, 0.87654321));

        var raw = Encoding.UTF8.GetString(await ReadDatabaseFilesAsync());

        Assert.DoesNotContain("StartTicks", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("EndTicks", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("Confidence", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("ProfileVersion", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("DetectorVersion", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("PolicyVersion", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VisualEvidenceSchema_ContainsOnlyOpaqueIdentitySessionAndCiphertext()
    {
        var columns = await TableColumnsAsync("anonymous_visual_evidence");

        Assert.Equal(["id", "session_id", "payload_nonce", "payload_cipher", "payload_tag"], columns);
        var joined = string.Join('|', columns);
        foreach (var forbidden in new[]
                 {
                     "start", "end", "range", "confidence", "provider", "version", "name", "text", "ocr",
                     "roi", "coordinate", "hwnd", "pid", "url", "title", "image", "pixel", "buffer"
                 })
            Assert.DoesNotContain(forbidden, joined, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeleteSession_CascadesAnonymousVisualEvidence()
    {
        var session = NewSession();
        await _store.CreateSessionAsync(session);
        await _store.SaveAnonymousVisualEvidenceAsync(Activity(session.Id, Guid.NewGuid()));

        await _store.DeleteSessionAsync(session.Id);

        Assert.Equal(0, await ScalarCountAsync(DatabasePath, "anonymous_visual_evidence"));
        var restored = await _store.GetAnonymousVisualEvidenceAsync(session.Id);
        Assert.Equal(AnonymousVisualEvidenceReadStatus.Loaded, restored.Status);
        Assert.Empty(restored.Intervals);
    }

    [Fact]
    public async Task GetAnonymousVisualEvidence_CorruptedCiphertext_ReturnsSafeStatus()
    {
        var session = NewSession();
        await _store.CreateSessionAsync(session);
        var evidence = Activity(session.Id, Guid.NewGuid());
        await _store.SaveAnonymousVisualEvidenceAsync(evidence);
        await CorruptCiphertextAsync(evidence.Id);

        var restored = await _store.GetAnonymousVisualEvidenceAsync(session.Id);

        Assert.Equal(AnonymousVisualEvidenceReadStatus.Corrupted, restored.Status);
        Assert.Empty(restored.Intervals);
    }

    [Fact]
    public async Task GetAnonymousVisualEvidence_UnknownPayloadVersion_ReturnsSafeStatus()
    {
        var session = NewSession();
        await _store.CreateSessionAsync(session);
        var evidence = Activity(session.Id, Guid.NewGuid());
        await _store.SaveAnonymousVisualEvidenceAsync(evidence);
        var unknownPayload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            PayloadVersion = 999,
            Kind = (int)AnonymousVisualEvidenceKind.Activity,
            Availability = (int)AnonymousVisualAnalysisAvailability.Available,
            StartTicks = 0L,
            EndTicks = TimeSpan.TicksPerSecond,
            Confidence = 1d,
            Provider = (int)MeetingProvider.GoogleMeet,
            ProfileVersion = 1,
            EvidenceVersion = 1,
            DetectorVersion = 1,
            PolicyVersion = 1
        });
        await ReplacePayloadAsync(evidence, _protector.Protect(unknownPayload, AssociatedData(evidence)));

        var restored = await _store.GetAnonymousVisualEvidenceAsync(session.Id);

        Assert.Equal(AnonymousVisualEvidenceReadStatus.UnsupportedVersion, restored.Status);
        Assert.Empty(restored.Intervals);
    }

    [Fact]
    public async Task GetAnonymousVisualEvidence_PayloadsSwappedBetweenRows_FailsAadAuthentication()
    {
        var session = NewSession();
        await _store.CreateSessionAsync(session);
        var first = Activity(session.Id, Guid.ParseExact("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", "N"), 1, 2);
        var second = Activity(session.Id, Guid.ParseExact("ffffffffffffffffffffffffffffffff", "N"), 3, 4);
        await _store.SaveAnonymousVisualEvidenceAsync(first);
        await _store.SaveAnonymousVisualEvidenceAsync(second);
        await SwapPayloadsAsync(first.Id, second.Id);

        var restored = await _store.GetAnonymousVisualEvidenceAsync(session.Id);

        Assert.Equal(AnonymousVisualEvidenceReadStatus.Corrupted, restored.Status);
        Assert.Empty(restored.Intervals);
    }

    [Fact]
    public async Task SaveAndDeleteSession_ConcurrentRace_LeavesNoVisualEvidenceOrOrphan()
    {
        var session = NewSession();
        await _store.CreateSessionAsync(session);
        var evidence = Activity(session.Id, Guid.NewGuid());
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var saveTask = Task.Run(async () =>
        {
            await start.Task;
            return await _store.SaveAnonymousVisualEvidenceAsync(evidence);
        });
        var deleteTask = Task.Run(async () =>
        {
            await start.Task;
            await _store.DeleteSessionAsync(session.Id);
        });

        start.SetResult(true);
        await Task.WhenAll(saveTask, deleteTask);

        Assert.Equal(0, await ScalarCountAsync(DatabasePath, "sessions"));
        Assert.Equal(0, await ScalarCountAsync(DatabasePath, "anonymous_visual_evidence"));
    }

    [Fact]
    public async Task InitializeAsync_LegacyDatabase_PreservesExistingSessionAndSegment()
    {
        var legacyPath = Path.Combine(_directory, "legacy.db");
        const string sessionId = "legacy-session";
        const string segmentId = "legacy-segment";
        await CreateLegacyDatabaseAsync(legacyPath, sessionId, segmentId);
        var migratedStore = new SqliteSessionStore(legacyPath, _protector);

        await migratedStore.InitializeAsync();

        var session = Assert.Single(await migratedStore.ListSessionsAsync());
        var segment = Assert.Single(await migratedStore.GetSegmentsAsync(sessionId));
        Assert.Equal("Legacy meeting", session.Title);
        Assert.Equal("Legacy transcript", segment.Text);
        Assert.Equal(MeetingProvider.NotSelected, session.MeetingProvider);
        var evidence = Activity(sessionId, Guid.NewGuid());
        Assert.True(await migratedStore.SaveAnonymousVisualEvidenceAsync(evidence));
        Assert.Equal(evidence, Assert.Single((await migratedStore.GetAnonymousVisualEvidenceAsync(sessionId)).Intervals));
    }

    private static MeetingSession NewSession() => new(
        Guid.NewGuid().ToString("N"),
        "Meeting",
        DateTimeOffset.UtcNow,
        null,
        SessionState.Recording);

    private static AnonymousVisualEvidenceInterval Activity(
        string sessionId,
        Guid id,
        double startSeconds = 1,
        double endSeconds = 2,
        double confidence = 0.95) =>
        AnonymousVisualEvidenceInterval.Activity(
            id,
            sessionId,
            TimeSpan.FromSeconds(startSeconds),
            TimeSpan.FromSeconds(endSeconds),
            confidence,
            new(MeetingProvider.GoogleMeet, 1, 1, 2, 3));

    private static AnonymousVisualEvidenceInterval Coverage(
        string sessionId,
        Guid id,
        double startSeconds,
        double endSeconds) =>
        AnonymousVisualEvidenceInterval.Coverage(
            id,
            sessionId,
            TimeSpan.FromSeconds(startSeconds),
            TimeSpan.FromSeconds(endSeconds),
            AnonymousVisualAnalysisAvailability.Available,
            0.9,
            new(MeetingProvider.MicrosoftTeams, 1, 1, 2, 3));

    private async Task CorruptCiphertextAsync(Guid id)
    {
        await using var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE anonymous_visual_evidence SET payload_cipher=zeroblob(length(payload_cipher)) WHERE id=$id";
        command.Parameters.AddWithValue("$id", id.ToString("N"));
        await command.ExecuteNonQueryAsync();
    }

    private async Task ReplacePayloadAsync(AnonymousVisualEvidenceInterval evidence, EncryptedPayload payload)
    {
        await using var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE anonymous_visual_evidence SET payload_nonce=$n,payload_cipher=$c,payload_tag=$t WHERE id=$id";
        command.Parameters.AddWithValue("$id", evidence.Id.ToString("N"));
        command.Parameters.AddWithValue("$n", payload.Nonce);
        command.Parameters.AddWithValue("$c", payload.Ciphertext);
        command.Parameters.AddWithValue("$t", payload.Tag);
        await command.ExecuteNonQueryAsync();
    }

    private async Task SwapPayloadsAsync(Guid firstId, Guid secondId)
    {
        await using var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False");
        await connection.OpenAsync();
        var payloads = new Dictionary<string, EncryptedPayload>(StringComparer.Ordinal);
        await using var select = connection.CreateCommand();
        select.CommandText = "SELECT id,payload_nonce,payload_cipher,payload_tag FROM anonymous_visual_evidence WHERE id IN ($first,$second)";
        select.Parameters.AddWithValue("$first", firstId.ToString("N"));
        select.Parameters.AddWithValue("$second", secondId.ToString("N"));
        await using (var reader = await select.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                payloads.Add(
                    reader.GetString(0),
                    new(
                        (byte[])reader.GetValue(1),
                        (byte[])reader.GetValue(2),
                        (byte[])reader.GetValue(3)));
        }

        await using var transaction = await connection.BeginTransactionAsync();
        await WritePayloadAsync(connection, transaction, firstId, payloads[secondId.ToString("N")]);
        await WritePayloadAsync(connection, transaction, secondId, payloads[firstId.ToString("N")]);
        await transaction.CommitAsync();
    }

    private static async Task WritePayloadAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        Guid id,
        EncryptedPayload payload)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "UPDATE anonymous_visual_evidence SET payload_nonce=$n,payload_cipher=$c,payload_tag=$t WHERE id=$id";
        command.Parameters.AddWithValue("$id", id.ToString("N"));
        command.Parameters.AddWithValue("$n", payload.Nonce);
        command.Parameters.AddWithValue("$c", payload.Ciphertext);
        command.Parameters.AddWithValue("$t", payload.Tag);
        await command.ExecuteNonQueryAsync();
    }

    private async Task CreateLegacyDatabaseAsync(string path, string sessionId, string segmentId)
    {
        var title = _protector.Protect(Encoding.UTF8.GetBytes("Legacy meeting"), $"session:{sessionId}:title");
        var text = _protector.Protect(Encoding.UTF8.GetBytes("Legacy transcript"), $"segment:{segmentId}:text");
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA foreign_keys=ON;
            CREATE TABLE sessions (
              id TEXT PRIMARY KEY, title_nonce BLOB NOT NULL, title_cipher BLOB NOT NULL, title_tag BLOB NOT NULL,
              started_at TEXT NOT NULL, ended_at TEXT NULL, state INTEGER NOT NULL);
            CREATE TABLE segments (
              id TEXT PRIMARY KEY, session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
              source INTEGER NOT NULL, sequence INTEGER NOT NULL, start_ms INTEGER NOT NULL, end_ms INTEGER NOT NULL,
              text_nonce BLOB NOT NULL, text_cipher BLOB NOT NULL, text_tag BLOB NOT NULL, created_at TEXT NOT NULL,
              UNIQUE(id));
            INSERT INTO sessions(id,title_nonce,title_cipher,title_tag,started_at,ended_at,state)
            VALUES($session,$titleNonce,$titleCipher,$titleTag,$started,NULL,$state);
            INSERT INTO segments(id,session_id,source,sequence,start_ms,end_ms,text_nonce,text_cipher,text_tag,created_at)
            VALUES($segment,$session,$source,1,0,1000,$textNonce,$textCipher,$textTag,$created);
            """;
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$titleNonce", title.Nonce);
        command.Parameters.AddWithValue("$titleCipher", title.Ciphertext);
        command.Parameters.AddWithValue("$titleTag", title.Tag);
        command.Parameters.AddWithValue("$started", DateTimeOffset.UnixEpoch.ToString("O"));
        command.Parameters.AddWithValue("$state", (int)SessionState.Completed);
        command.Parameters.AddWithValue("$segment", segmentId);
        command.Parameters.AddWithValue("$source", (int)AudioSourceKind.SystemOutput);
        command.Parameters.AddWithValue("$textNonce", text.Nonce);
        command.Parameters.AddWithValue("$textCipher", text.Ciphertext);
        command.Parameters.AddWithValue("$textTag", text.Tag);
        command.Parameters.AddWithValue("$created", DateTimeOffset.UnixEpoch.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private async Task<string[]> TableColumnsAsync(string table)
    {
        await using var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table})";
        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) columns.Add(reader.GetString(1));
        return [.. columns];
    }

    private async Task<byte[]> ReadDatabaseFilesAsync()
    {
        var files = Directory.GetFiles(_directory, Path.GetFileName(DatabasePath) + "*");
        await using var memory = new MemoryStream();
        foreach (var file in files.Order(StringComparer.Ordinal))
            await memory.WriteAsync(await File.ReadAllBytesAsync(file));
        return memory.ToArray();
    }

    private static async Task<long> ScalarCountAsync(string databasePath, string table)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table}";
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static string AssociatedData(AnonymousVisualEvidenceInterval evidence) =>
        $"anonymous-visual-evidence:{evidence.SessionId}:{evidence.Id:N}:payload";
}
