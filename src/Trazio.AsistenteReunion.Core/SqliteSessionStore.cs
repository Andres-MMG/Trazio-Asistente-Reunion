using System.Text;
using Microsoft.Data.Sqlite;

namespace Trazio.AsistenteReunion.Core;

public sealed partial class SqliteSessionStore(string databasePath, IContentProtector protector)
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false, DefaultTimeout = 5 }.ToString();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA foreign_keys=ON;
            CREATE TABLE IF NOT EXISTS sessions (
              id TEXT PRIMARY KEY, title_nonce BLOB NOT NULL, title_cipher BLOB NOT NULL, title_tag BLOB NOT NULL,
              started_at TEXT NOT NULL, ended_at TEXT NULL, state INTEGER NOT NULL,
              local_speaker_nonce BLOB NULL, local_speaker_cipher BLOB NULL, local_speaker_tag BLOB NULL,
              meeting_provider INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS segments (
              id TEXT PRIMARY KEY, session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
              source INTEGER NOT NULL, sequence INTEGER NOT NULL, start_ms INTEGER NOT NULL, end_ms INTEGER NOT NULL,
              text_nonce BLOB NOT NULL, text_cipher BLOB NOT NULL, text_tag BLOB NOT NULL, created_at TEXT NOT NULL,
              UNIQUE(id));
            CREATE TABLE IF NOT EXISTS pending_audio (
              id TEXT PRIMARY KEY, session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
              source INTEGER NOT NULL, sequence INTEGER NOT NULL, captured_at TEXT NOT NULL, expires_at TEXT NOT NULL,
              audio_nonce BLOB NOT NULL, audio_cipher BLOB NOT NULL, audio_tag BLOB NOT NULL);
            CREATE TABLE IF NOT EXISTS archived_audio (
              id TEXT PRIMARY KEY, session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
              source INTEGER NOT NULL, sequence INTEGER NOT NULL, started_at TEXT NOT NULL,
              duration_ms INTEGER NOT NULL, relative_path TEXT NOT NULL UNIQUE, encrypted_bytes INTEGER NOT NULL,
              UNIQUE(session_id, source, sequence));
            CREATE INDEX IF NOT EXISTS ix_archived_audio_retention ON archived_audio(started_at);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await EnsureColumnAsync(connection, "sessions", "local_speaker_nonce", "BLOB NULL", cancellationToken);
        await EnsureColumnAsync(connection, "sessions", "local_speaker_cipher", "BLOB NULL", cancellationToken);
        await EnsureColumnAsync(connection, "sessions", "local_speaker_tag", "BLOB NULL", cancellationToken);
        await EnsureColumnAsync(connection, "sessions", "meeting_provider", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, "segments", "speaker_nonce", "BLOB NULL", cancellationToken);
        await EnsureColumnAsync(connection, "segments", "speaker_cipher", "BLOB NULL", cancellationToken);
        await EnsureColumnAsync(connection, "segments", "speaker_tag", "BLOB NULL", cancellationToken);
        await InitializeReviewSchemaAsync(connection, cancellationToken);
        await InitializeBatchReviewSchemaAsync(connection, cancellationToken);
        await InitializeModelRevisionSchemaAsync(connection, cancellationToken);
        await InitializeAnonymousVisualEvidenceSchemaAsync(connection, cancellationToken);
        await InitializeSegmentAnnotationSchemaAsync(connection, cancellationToken);
    }

    public async Task CreateSessionAsync(MeetingSession session, CancellationToken cancellationToken = default)
    {
        var encrypted = protector.Protect(Encoding.UTF8.GetBytes(session.Title), $"session:{session.Id}:title");
        var speaker = ProtectOptional(session.LocalSpeakerName, $"session:{session.Id}:local-speaker");
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sessions(id,title_nonce,title_cipher,title_tag,started_at,ended_at,state,local_speaker_nonce,local_speaker_cipher,local_speaker_tag,meeting_provider)
            VALUES($id,$n,$c,$t,$start,$end,$state,$sn,$sc,$st,$provider)
            """;
        command.Parameters.AddWithValue("$id", session.Id);
        AddPayload(command, encrypted);
        AddOptionalPayload(command, speaker);
        command.Parameters.AddWithValue("$start", session.StartedAt.ToString("O"));
        command.Parameters.AddWithValue("$end", (object?)session.EndedAt?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$state", (int)session.State);
        command.Parameters.AddWithValue("$provider", (int)session.MeetingProvider);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> UpdateSessionTitleAsync(string id, string title, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("El título de la reunión no puede estar vacío.", nameof(title));
        var encrypted = protector.Protect(Encoding.UTF8.GetBytes(title.Trim()), $"session:{id}:title");
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE sessions SET title_nonce=$n, title_cipher=$c, title_tag=$t WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        AddPayload(command, encrypted);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task CompleteSessionAsync(string id, SessionState state, DateTimeOffset endedAt, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE sessions SET state=$state, ended_at=$ended WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$state", (int)state);
        command.Parameters.AddWithValue("$ended", endedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> SaveSegmentAsync(TranscriptSegment segment, CancellationToken cancellationToken = default)
    {
        var encrypted = protector.Protect(Encoding.UTF8.GetBytes(segment.Text), $"segment:{segment.Id}:text");
        var speaker = segment.Source == AudioSourceKind.Microphone
            ? ProtectOptional(segment.SpeakerName, $"segment:{segment.Id}:speaker")
            : null;
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
          INSERT OR IGNORE INTO segments(id,session_id,source,sequence,start_ms,end_ms,text_nonce,text_cipher,text_tag,created_at,speaker_nonce,speaker_cipher,speaker_tag)
          VALUES($id,$session,$source,$sequence,$start,$end,$n,$c,$t,$created,$sn,$sc,$st)
          """;
        command.Parameters.AddWithValue("$id", segment.Id);
        command.Parameters.AddWithValue("$session", segment.SessionId);
        command.Parameters.AddWithValue("$source", (int)segment.Source);
        command.Parameters.AddWithValue("$sequence", segment.Sequence);
        command.Parameters.AddWithValue("$start", (long)segment.Start.TotalMilliseconds);
        command.Parameters.AddWithValue("$end", (long)segment.End.TotalMilliseconds);
        AddPayload(command, encrypted);
        AddOptionalPayload(command, speaker);
        command.Parameters.AddWithValue("$created", segment.CreatedAt.ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task SavePendingAsync(AudioChunk chunk, DateTimeOffset expiresAt, CancellationToken cancellationToken = default)
    {
        var encrypted = protector.Protect(chunk.Pcm16, $"pending:{chunk.Id}:audio");
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
          INSERT OR REPLACE INTO pending_audio VALUES($id,$session,$source,$sequence,$captured,$expires,$n,$c,$t)
          """;
        command.Parameters.AddWithValue("$id", chunk.Id);
        command.Parameters.AddWithValue("$session", chunk.SessionId);
        command.Parameters.AddWithValue("$source", (int)chunk.Source);
        command.Parameters.AddWithValue("$sequence", chunk.Sequence);
        command.Parameters.AddWithValue("$captured", chunk.CapturedAt.ToString("O"));
        command.Parameters.AddWithValue("$expires", expiresAt.ToString("O"));
        AddPayload(command, encrypted);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeletePendingAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM pending_audio WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AudioChunk>> GetPendingAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var result = new List<AudioChunk>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,source,sequence,captured_at,audio_nonce,audio_cipher,audio_tag FROM pending_audio WHERE session_id=$session ORDER BY source,sequence";
        command.Parameters.AddWithValue("$session", sessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetString(0);
            var audio = protector.Unprotect(ReadPayload(reader, 4), $"pending:{id}:audio");
            result.Add(new(id, sessionId, (AudioSourceKind)reader.GetInt32(1), reader.GetInt64(2), DateTimeOffset.Parse(reader.GetString(3)), audio));
        }
        return result;
    }

    public async Task<int> DeleteExpiredPendingAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM pending_audio
            WHERE expires_at < $now
              AND session_id IN (SELECT id FROM sessions WHERE state IN ($completed, $interrupted))
            """;
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$completed", (int)SessionState.Completed);
        command.Parameters.AddWithValue("$interrupted", (int)SessionState.Interrupted);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> NormalizeStaleRecordingSessionsAsync(DateTimeOffset interruptedAt, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            UPDATE sessions
            SET state = $interrupted, ended_at = COALESCE(ended_at, $ended)
            WHERE state = $recording
            """;
        command.Parameters.AddWithValue("$interrupted", (int)SessionState.Interrupted);
        command.Parameters.AddWithValue("$recording", (int)SessionState.Recording);
        command.Parameters.AddWithValue("$ended", interruptedAt.ToString("O"));
        var changed = await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return changed;
    }

    public async Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<SessionSummary>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,title_nonce,title_cipher,title_tag,started_at,ended_at,state,local_speaker_nonce,local_speaker_cipher,local_speaker_tag,meeting_provider FROM sessions ORDER BY started_at DESC";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetString(0);
            var title = Encoding.UTF8.GetString(protector.Unprotect(ReadPayload(reader, 1), $"session:{id}:title"));
            var speaker = UnprotectOptional(reader, 7, $"session:{id}:local-speaker");
            result.Add(new(id, title, DateTimeOffset.Parse(reader.GetString(4)), reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5)), (SessionState)reader.GetInt32(6), speaker, ReadMeetingProvider(reader.GetInt32(10))));
        }
        return result;
    }

    public async Task<IReadOnlyList<TranscriptSegment>> GetSegmentsAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var result = new List<TranscriptSegment>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,source,sequence,start_ms,end_ms,text_nonce,text_cipher,text_tag,created_at,speaker_nonce,speaker_cipher,speaker_tag FROM segments WHERE session_id=$session ORDER BY start_ms,source";
        command.Parameters.AddWithValue("$session", sessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetString(0);
            var text = Encoding.UTF8.GetString(protector.Unprotect(ReadPayload(reader, 5), $"segment:{id}:text"));
            var source = (AudioSourceKind)reader.GetInt32(1);
            var speaker = source == AudioSourceKind.Microphone ? UnprotectOptional(reader, 9, $"segment:{id}:speaker") : null;
            result.Add(new(id, sessionId, source, reader.GetInt64(2), TimeSpan.FromMilliseconds(reader.GetInt64(3)), TimeSpan.FromMilliseconds(reader.GetInt64(4)), text, DateTimeOffset.Parse(reader.GetString(8)), speaker));
        }
        return result;
    }

    public async Task DeleteSessionAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM sessions WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveArchivedAudioAsync(ArchivedAudioChunk chunk, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO archived_audio(id,session_id,source,sequence,started_at,duration_ms,relative_path,encrypted_bytes)
            VALUES($id,$session,$source,$sequence,$started,$duration,$path,$bytes)
            """;
        command.Parameters.AddWithValue("$id", chunk.Id);
        command.Parameters.AddWithValue("$session", chunk.SessionId);
        command.Parameters.AddWithValue("$source", (int)chunk.Source);
        command.Parameters.AddWithValue("$sequence", chunk.Sequence);
        command.Parameters.AddWithValue("$started", chunk.StartedAt.ToString("O"));
        command.Parameters.AddWithValue("$duration", (long)chunk.Duration.TotalMilliseconds);
        command.Parameters.AddWithValue("$path", chunk.RelativePath);
        command.Parameters.AddWithValue("$bytes", chunk.EncryptedBytes);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ArchivedAudioChunk>> GetArchivedAudioAsync(
        string sessionId, AudioSourceKind? source = null, CancellationToken cancellationToken = default)
    {
        var result = new List<ArchivedAudioChunk>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,source,sequence,started_at,duration_ms,relative_path,encrypted_bytes
            FROM archived_audio WHERE session_id=$session AND ($source IS NULL OR source=$source)
            ORDER BY source,sequence
            """;
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$source", source is null ? DBNull.Value : (int)source.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new(reader.GetString(0), sessionId, (AudioSourceKind)reader.GetInt32(1), reader.GetInt64(2),
                DateTimeOffset.Parse(reader.GetString(3)), TimeSpan.FromMilliseconds(reader.GetInt64(4)), reader.GetString(5), reader.GetInt64(6)));
        return result;
    }

    public async Task<IReadOnlyList<AudioArchiveSummary>> GetAudioArchiveSummaryAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var result = new List<AudioArchiveSummary>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT source,COUNT(*),COALESCE(SUM(duration_ms),0),COALESCE(SUM(encrypted_bytes),0)
            FROM archived_audio WHERE session_id=$session GROUP BY source ORDER BY source
            """;
        command.Parameters.AddWithValue("$session", sessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new((AudioSourceKind)reader.GetInt32(0), reader.GetInt32(1), TimeSpan.FromMilliseconds(reader.GetInt64(2)), reader.GetInt64(3)));
        return result;
    }

    public async Task<IReadOnlyList<ArchivedAudioChunk>> GetPrunableArchivedAudioAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<ArchivedAudioChunk>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.id,a.session_id,a.source,a.sequence,a.started_at,a.duration_ms,a.relative_path,a.encrypted_bytes
            FROM archived_audio a JOIN sessions s ON s.id=a.session_id
            WHERE s.state IN ($completed,$interrupted) ORDER BY a.started_at,a.sequence
            """;
        command.Parameters.AddWithValue("$completed", (int)SessionState.Completed);
        command.Parameters.AddWithValue("$interrupted", (int)SessionState.Interrupted);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new(reader.GetString(0), reader.GetString(1), (AudioSourceKind)reader.GetInt32(2), reader.GetInt64(3),
                DateTimeOffset.Parse(reader.GetString(4)), TimeSpan.FromMilliseconds(reader.GetInt64(5)), reader.GetString(6), reader.GetInt64(7)));
        return result;
    }

    public async Task<long> GetTotalArchivedAudioBytesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(SUM(encrypted_bytes),0) FROM archived_audio";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task<bool> DeleteArchivedAudioMetadataAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM archived_audio WHERE id=$id AND session_id IN
            (SELECT id FROM sessions WHERE state IN ($completed,$interrupted))
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$completed", (int)SessionState.Completed);
        command.Parameters.AddWithValue("$interrupted", (int)SessionState.Interrupted);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(ct);
            await using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
            await pragma.ExecuteNonQueryAsync(ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }


    private EncryptedPayload? ProtectOptional(string? value, string purpose) =>
        string.IsNullOrWhiteSpace(value) ? null : protector.Protect(Encoding.UTF8.GetBytes(value.Trim()), purpose);

    private string? UnprotectOptional(SqliteDataReader reader, int offset, string purpose)
    {
        if (reader.IsDBNull(offset) || reader.IsDBNull(offset + 1) || reader.IsDBNull(offset + 2)) return null;
        return Encoding.UTF8.GetString(protector.Unprotect(ReadPayload(reader, offset), purpose));
    }

    private static void AddOptionalPayload(SqliteCommand command, EncryptedPayload? payload)
    {
        command.Parameters.AddWithValue("$sn", payload is null ? DBNull.Value : payload.Nonce);
        command.Parameters.AddWithValue("$sc", payload is null ? DBNull.Value : payload.Ciphertext);
        command.Parameters.AddWithValue("$st", payload is null ? DBNull.Value : payload.Tag);
    }

    private static async Task EnsureColumnAsync(SqliteConnection connection, string table, string column, string definition, CancellationToken cancellationToken)
    {
        await using var schema = connection.CreateCommand();
        schema.CommandText = $"PRAGMA table_info({table})";
        await using var reader = await schema.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return;
        await reader.DisposeAsync();
        await using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }
    private static void AddPayload(SqliteCommand command, EncryptedPayload payload)
    {
        command.Parameters.AddWithValue("$n", payload.Nonce);
        command.Parameters.AddWithValue("$c", payload.Ciphertext);
        command.Parameters.AddWithValue("$t", payload.Tag);
    }

    private static EncryptedPayload ReadPayload(SqliteDataReader reader, int offset) =>
        new((byte[])reader.GetValue(offset), (byte[])reader.GetValue(offset + 1), (byte[])reader.GetValue(offset + 2));

    private static MeetingProvider ReadMeetingProvider(int value) => value switch
    {
        (int)MeetingProvider.NotSelected => MeetingProvider.NotSelected,
        (int)MeetingProvider.GoogleMeet => MeetingProvider.GoogleMeet,
        (int)MeetingProvider.MicrosoftTeams => MeetingProvider.MicrosoftTeams,
        (int)MeetingProvider.Other => MeetingProvider.Other,
        _ => MeetingProvider.Other
    };
}

