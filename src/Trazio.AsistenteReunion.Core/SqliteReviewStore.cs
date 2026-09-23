using System.Text;
using Microsoft.Data.Sqlite;

namespace Trazio.AsistenteReunion.Core;

public sealed partial class SqliteSessionStore
{
    private static async Task InitializeReviewSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS transcript_corrections (
              id TEXT PRIMARY KEY,
              segment_id TEXT NOT NULL REFERENCES segments(id) ON DELETE CASCADE,
              session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
              revision INTEGER NOT NULL,
              action INTEGER NOT NULL,
              corrected_nonce BLOB NULL, corrected_cipher BLOB NULL, corrected_tag BLOB NULL,
              editor_nonce BLOB NULL, editor_cipher BLOB NULL, editor_tag BLOB NULL,
              created_at TEXT NOT NULL,
              UNIQUE(segment_id, revision));
            CREATE INDEX IF NOT EXISTS ix_corrections_session ON transcript_corrections(session_id, segment_id, revision);
            CREATE TABLE IF NOT EXISTS glossary_entries (
              id TEXT PRIMARY KEY,
              preferred_nonce BLOB NOT NULL, preferred_cipher BLOB NOT NULL, preferred_tag BLOB NOT NULL,
              mistaken_nonce BLOB NOT NULL, mistaken_cipher BLOB NOT NULL, mistaken_tag BLOB NOT NULL,
              category_nonce BLOB NOT NULL, category_cipher BLOB NOT NULL, category_tag BLOB NOT NULL,
              is_active INTEGER NOT NULL,
              source_correction_id TEXT NOT NULL REFERENCES transcript_corrections(id) ON DELETE CASCADE,
              created_at TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_glossary_active ON glossary_entries(is_active, created_at);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<TranscriptCorrection> SaveCorrectionAsync(
        string segmentId,
        string correctedText,
        string? editorName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(correctedText))
            throw new InvalidOperationException("El texto corregido no puede estar vacío.");
        return await AppendCorrectionAsync(segmentId, CorrectionAction.SetText, correctedText.Trim(), editorName, cancellationToken);
    }

    public async Task<TranscriptCorrection> UndoCorrectionAsync(
        string segmentId,
        string? editorName,
        CancellationToken cancellationToken = default)
    {
        var history = await GetCorrectionsAsync(segmentId, cancellationToken);
        if (history.Count == 0 || history[^1].Action == CorrectionAction.Undo)
            throw new InvalidOperationException("Este segmento no tiene una corrección activa para deshacer.");
        return await AppendCorrectionAsync(segmentId, CorrectionAction.Undo, null, editorName, cancellationToken);
    }

    public async Task<IReadOnlyList<TranscriptCorrection>> GetCorrectionsAsync(
        string segmentId,
        CancellationToken cancellationToken = default)
    {
        var result = new List<TranscriptCorrection>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,session_id,revision,action,corrected_nonce,corrected_cipher,corrected_tag,
                   editor_nonce,editor_cipher,editor_tag,created_at
            FROM transcript_corrections WHERE segment_id=$segment ORDER BY revision
            """;
        command.Parameters.AddWithValue("$segment", segmentId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetString(0);
            result.Add(new(
                id,
                segmentId,
                reader.GetString(1),
                reader.GetInt32(2),
                (CorrectionAction)reader.GetInt32(3),
                UnprotectReviewValue(reader, 4, $"correction:{id}:text"),
                UnprotectReviewValue(reader, 7, $"correction:{id}:editor"),
                DateTimeOffset.Parse(reader.GetString(10))));
        }
        return result;
    }

    public async Task<IReadOnlyList<ReviewedTranscriptSegment>> GetReviewedSegmentsAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var segments = await GetSegmentsAsync(sessionId, cancellationToken);
        var latest = new Dictionary<string, TranscriptCorrection>(StringComparer.Ordinal);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,segment_id,revision,action,corrected_nonce,corrected_cipher,corrected_tag,
                   editor_nonce,editor_cipher,editor_tag,created_at
            FROM transcript_corrections WHERE session_id=$session ORDER BY segment_id,revision
            """;
        command.Parameters.AddWithValue("$session", sessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetString(0);
            var segmentId = reader.GetString(1);
            latest[segmentId] = new(
                id,
                segmentId,
                sessionId,
                reader.GetInt32(2),
                (CorrectionAction)reader.GetInt32(3),
                UnprotectReviewValue(reader, 4, $"correction:{id}:text"),
                UnprotectReviewValue(reader, 7, $"correction:{id}:editor"),
                DateTimeOffset.Parse(reader.GetString(10)));
        }
        return segments.Select(segment => new ReviewedTranscriptSegment(
            segment,
            latest.GetValueOrDefault(segment.Id))).ToArray();
    }

    public async Task<GlossaryEntry> AddGlossaryEntryAsync(
        string preferredTerm,
        string mistakenForm,
        string category,
        bool isActive,
        string sourceCorrectionId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(preferredTerm) || string.IsNullOrWhiteSpace(mistakenForm) || string.IsNullOrWhiteSpace(category))
            throw new InvalidOperationException("Se requieren el término correcto, la forma incorrecta y la categoría.");

        await using var connection = await OpenAsync(cancellationToken);
        await using var source = connection.CreateCommand();
        source.CommandText = "SELECT action FROM transcript_corrections WHERE id=$id";
        source.Parameters.AddWithValue("$id", sourceCorrectionId);
        var action = await source.ExecuteScalarAsync(cancellationToken);
        if (action is null || Convert.ToInt32(action) != (int)CorrectionAction.SetText)
            throw new InvalidOperationException("Los términos del diccionario deben provenir de una corrección guardada.");

        var entry = new GlossaryEntry(
            Guid.NewGuid().ToString("N"),
            preferredTerm.Trim(),
            mistakenForm.Trim(),
            category.Trim(),
            isActive,
            sourceCorrectionId,
            DateTimeOffset.UtcNow);
        var preferred = protector.Protect(Encoding.UTF8.GetBytes(entry.PreferredTerm), $"glossary:{entry.Id}:preferred");
        var mistaken = protector.Protect(Encoding.UTF8.GetBytes(entry.MistakenForm), $"glossary:{entry.Id}:mistaken");
        var encryptedCategory = protector.Protect(Encoding.UTF8.GetBytes(entry.Category), $"glossary:{entry.Id}:category");
        await using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO glossary_entries(
              id,preferred_nonce,preferred_cipher,preferred_tag,
              mistaken_nonce,mistaken_cipher,mistaken_tag,
              category_nonce,category_cipher,category_tag,is_active,source_correction_id,created_at)
            VALUES($id,$pn,$pc,$pt,$mn,$mc,$mt,$cn,$cc,$ct,$active,$correction,$created)
            """;
        insert.Parameters.AddWithValue("$id", entry.Id);
        AddNamedPayload(insert, "p", preferred);
        AddNamedPayload(insert, "m", mistaken);
        AddNamedPayload(insert, "c", encryptedCategory);
        insert.Parameters.AddWithValue("$active", entry.IsActive ? 1 : 0);
        insert.Parameters.AddWithValue("$correction", entry.SourceCorrectionId);
        insert.Parameters.AddWithValue("$created", entry.CreatedAt.ToString("O"));
        await insert.ExecuteNonQueryAsync(cancellationToken);
        return entry;
    }

    public async Task<IReadOnlyList<GlossaryEntry>> ListGlossaryAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<GlossaryEntry>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,preferred_nonce,preferred_cipher,preferred_tag,
                   mistaken_nonce,mistaken_cipher,mistaken_tag,
                   category_nonce,category_cipher,category_tag,is_active,source_correction_id,created_at
            FROM glossary_entries ORDER BY created_at,id
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetString(0);
            result.Add(new(
                id,
                UnprotectRequired(reader, 1, $"glossary:{id}:preferred"),
                UnprotectRequired(reader, 4, $"glossary:{id}:mistaken"),
                UnprotectRequired(reader, 7, $"glossary:{id}:category"),
                reader.GetInt32(10) != 0,
                reader.GetString(11),
                DateTimeOffset.Parse(reader.GetString(12))));
        }
        return result;
    }

    private async Task<TranscriptCorrection> AppendCorrectionAsync(
        string segmentId,
        CorrectionAction action,
        string? correctedText,
        string? editorName,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var lookup = connection.CreateCommand();
        lookup.Transaction = transaction;
        lookup.CommandText = "SELECT session_id FROM segments WHERE id=$segment";
        lookup.Parameters.AddWithValue("$segment", segmentId);
        var sessionId = await lookup.ExecuteScalarAsync(cancellationToken) as string
            ?? throw new InvalidOperationException("El segmento de la transcripción ya no existe.");
        await using var revisionCommand = connection.CreateCommand();
        revisionCommand.Transaction = transaction;
        revisionCommand.CommandText = "SELECT COALESCE(MAX(revision),0)+1 FROM transcript_corrections WHERE segment_id=$segment";
        revisionCommand.Parameters.AddWithValue("$segment", segmentId);
        var revision = Convert.ToInt32(await revisionCommand.ExecuteScalarAsync(cancellationToken));
        var correction = new TranscriptCorrection(
            Guid.NewGuid().ToString("N"), segmentId, sessionId, revision, action,
            correctedText, string.IsNullOrWhiteSpace(editorName) ? null : editorName.Trim(), DateTimeOffset.UtcNow);
        var text = ProtectReviewValue(correction.CorrectedText, $"correction:{correction.Id}:text");
        var editor = ProtectReviewValue(correction.EditorName, $"correction:{correction.Id}:editor");
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO transcript_corrections(
              id,segment_id,session_id,revision,action,corrected_nonce,corrected_cipher,corrected_tag,
              editor_nonce,editor_cipher,editor_tag,created_at)
            VALUES($id,$segment,$session,$revision,$action,$tn,$tc,$tt,$en,$ec,$et,$created)
            """;
        insert.Parameters.AddWithValue("$id", correction.Id);
        insert.Parameters.AddWithValue("$segment", correction.SegmentId);
        insert.Parameters.AddWithValue("$session", correction.SessionId);
        insert.Parameters.AddWithValue("$revision", correction.Revision);
        insert.Parameters.AddWithValue("$action", (int)correction.Action);
        AddNamedOptionalPayload(insert, "t", text);
        AddNamedOptionalPayload(insert, "e", editor);
        insert.Parameters.AddWithValue("$created", correction.CreatedAt.ToString("O"));
        await insert.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return correction;
    }

    private EncryptedPayload? ProtectReviewValue(string? value, string purpose) =>
        string.IsNullOrWhiteSpace(value) ? null : protector.Protect(Encoding.UTF8.GetBytes(value), purpose);

    private string? UnprotectReviewValue(SqliteDataReader reader, int offset, string purpose) =>
        reader.IsDBNull(offset) ? null : UnprotectRequired(reader, offset, purpose);

    private string UnprotectRequired(SqliteDataReader reader, int offset, string purpose) =>
        Encoding.UTF8.GetString(protector.Unprotect(ReadPayload(reader, offset), purpose));

    private static void AddNamedPayload(SqliteCommand command, string prefix, EncryptedPayload payload)
    {
        command.Parameters.AddWithValue($"${prefix}n", payload.Nonce);
        command.Parameters.AddWithValue($"${prefix}c", payload.Ciphertext);
        command.Parameters.AddWithValue($"${prefix}t", payload.Tag);
    }

    private static void AddNamedOptionalPayload(SqliteCommand command, string prefix, EncryptedPayload? payload)
    {
        command.Parameters.AddWithValue($"${prefix}n", payload is null ? DBNull.Value : payload.Nonce);
        command.Parameters.AddWithValue($"${prefix}c", payload is null ? DBNull.Value : payload.Ciphertext);
        command.Parameters.AddWithValue($"${prefix}t", payload is null ? DBNull.Value : payload.Tag);
    }
}