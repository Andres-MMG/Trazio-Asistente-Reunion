using System.Text;
using Microsoft.Data.Sqlite;

namespace Trazio.AsistenteReunion.Core;

public sealed partial class SqliteSessionStore
{
    private static async Task InitializeSegmentAnnotationSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS segment_annotations (
              id TEXT PRIMARY KEY,
              segment_id TEXT NOT NULL REFERENCES segments(id) ON DELETE CASCADE,
              session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
              kind INTEGER NOT NULL,
              text_nonce BLOB NOT NULL, text_cipher BLOB NOT NULL, text_tag BLOB NOT NULL,
              status INTEGER NOT NULL,
              created_at TEXT NOT NULL,
              updated_at TEXT NOT NULL,
              CHECK(kind IN (0,1,2)),
              CHECK(status IN (0,1)));
            CREATE INDEX IF NOT EXISTS ix_segment_annotations_session
              ON segment_annotations(session_id, segment_id, created_at, id);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<SegmentAnnotation> AddSegmentAnnotationAsync(
        string segmentId,
        SegmentAnnotationKind kind,
        string text,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(segmentId);
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        var normalizedText = SegmentAnnotationLimits.NormalizeText(text);

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var lookup = connection.CreateCommand();
        lookup.Transaction = transaction;
        lookup.CommandText = "SELECT session_id FROM segments WHERE id=$segment";
        lookup.Parameters.AddWithValue("$segment", segmentId);
        var sessionId = await lookup.ExecuteScalarAsync(cancellationToken) as string
            ?? throw new InvalidOperationException("El segmento seleccionado ya no existe.");

        await using var count = connection.CreateCommand();
        count.Transaction = transaction;
        count.CommandText = "SELECT COUNT(*) FROM segment_annotations WHERE session_id=$session";
        count.Parameters.AddWithValue("$session", sessionId);
        if (Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken)) >=
            SegmentAnnotationLimits.MaximumAnnotationsPerSession)
            throw new InvalidOperationException(
                $"La reunión alcanzó el límite de {SegmentAnnotationLimits.MaximumAnnotationsPerSession} anotaciones.");

        var now = DateTimeOffset.UtcNow;
        var annotation = new SegmentAnnotation(
            Guid.NewGuid().ToString("N"),
            segmentId,
            sessionId,
            kind,
            normalizedText,
            SegmentAnnotationStatus.Open,
            now,
            now);
        var encrypted = protector.Protect(
            Encoding.UTF8.GetBytes(annotation.Text),
            $"segment-annotation:{annotation.Id}:text");
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO segment_annotations(
              id,segment_id,session_id,kind,text_nonce,text_cipher,text_tag,status,created_at,updated_at)
            VALUES($id,$segment,$session,$kind,$n,$c,$t,$status,$created,$updated)
            """;
        insert.Parameters.AddWithValue("$id", annotation.Id);
        insert.Parameters.AddWithValue("$segment", annotation.SegmentId);
        insert.Parameters.AddWithValue("$session", annotation.SessionId);
        insert.Parameters.AddWithValue("$kind", (int)annotation.Kind);
        insert.Parameters.AddWithValue("$n", encrypted.Nonce);
        insert.Parameters.AddWithValue("$c", encrypted.Ciphertext);
        insert.Parameters.AddWithValue("$t", encrypted.Tag);
        insert.Parameters.AddWithValue("$status", (int)annotation.Status);
        insert.Parameters.AddWithValue("$created", annotation.CreatedAt.ToString("O"));
        insert.Parameters.AddWithValue("$updated", annotation.UpdatedAt.ToString("O"));
        await insert.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return annotation;
    }

    public async Task<IReadOnlyList<SegmentAnnotation>> ListSegmentAnnotationsAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var result = new List<SegmentAnnotation>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,segment_id,kind,text_nonce,text_cipher,text_tag,status,created_at,updated_at
            FROM segment_annotations
            WHERE session_id=$session
            ORDER BY created_at,id
            """;
        command.Parameters.AddWithValue("$session", sessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetString(0);
            var kind = ReadSegmentAnnotationKind(reader.GetInt32(2));
            var status = ReadSegmentAnnotationStatus(reader.GetInt32(6));
            var text = Encoding.UTF8.GetString(protector.Unprotect(
                ReadPayload(reader, 3),
                $"segment-annotation:{id}:text"));
            result.Add(new(
                id,
                reader.GetString(1),
                sessionId,
                kind,
                text,
                status,
                DateTimeOffset.Parse(reader.GetString(7)),
                DateTimeOffset.Parse(reader.GetString(8))));
        }
        return result;
    }

    public async Task<SegmentAnnotationWriteStatus> SetSegmentAnnotationStatusAsync(
        string id,
        SegmentAnnotationStatus expectedStatus,
        SegmentAnnotationStatus newStatus,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (!Enum.IsDefined(expectedStatus)) throw new ArgumentOutOfRangeException(nameof(expectedStatus));
        if (!Enum.IsDefined(newStatus)) throw new ArgumentOutOfRangeException(nameof(newStatus));

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var lookup = connection.CreateCommand();
        lookup.Transaction = transaction;
        lookup.CommandText = "SELECT kind,status FROM segment_annotations WHERE id=$id";
        lookup.Parameters.AddWithValue("$id", id);
        await using var reader = await lookup.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return SegmentAnnotationWriteStatus.Missing;
        }
        var kind = ReadSegmentAnnotationKind(reader.GetInt32(0));
        var currentStatus = ReadSegmentAnnotationStatus(reader.GetInt32(1));
        await reader.DisposeAsync();
        if (kind is not SegmentAnnotationKind.FollowUp)
            throw new InvalidOperationException("Solo los seguimientos pueden cambiar entre pendiente y completado.");
        if (currentStatus != expectedStatus)
        {
            await transaction.RollbackAsync(cancellationToken);
            return SegmentAnnotationWriteStatus.StateChanged;
        }
        if (currentStatus == newStatus)
        {
            await transaction.RollbackAsync(cancellationToken);
            return SegmentAnnotationWriteStatus.AlreadyCurrent;
        }

        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE segment_annotations
            SET status=$status, updated_at=$updated
            WHERE id=$id AND status=$expected
            """;
        update.Parameters.AddWithValue("$status", (int)newStatus);
        update.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        update.Parameters.AddWithValue("$id", id);
        update.Parameters.AddWithValue("$expected", (int)expectedStatus);
        var changed = await update.ExecuteNonQueryAsync(cancellationToken);
        if (changed != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return SegmentAnnotationWriteStatus.StateChanged;
        }
        await transaction.CommitAsync(cancellationToken);
        return SegmentAnnotationWriteStatus.Applied;
    }

    public async Task<bool> DeleteSegmentAnnotationAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM segment_annotations WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static SegmentAnnotationKind ReadSegmentAnnotationKind(int value) => value switch
    {
        0 => SegmentAnnotationKind.Note,
        1 => SegmentAnnotationKind.Decision,
        2 => SegmentAnnotationKind.FollowUp,
        _ => throw new InvalidDataException("La anotación contiene un tipo desconocido.")
    };

    private static SegmentAnnotationStatus ReadSegmentAnnotationStatus(int value) => value switch
    {
        0 => SegmentAnnotationStatus.Open,
        1 => SegmentAnnotationStatus.Completed,
        _ => throw new InvalidDataException("La anotación contiene un estado desconocido.")
    };
}
