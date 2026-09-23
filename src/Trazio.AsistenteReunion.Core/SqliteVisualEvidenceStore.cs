using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Trazio.AsistenteReunion.Core;

public sealed partial class SqliteSessionStore
{
    private static async Task InitializeAnonymousVisualEvidenceSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS anonymous_visual_evidence (
              id TEXT PRIMARY KEY,
              session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
              payload_nonce BLOB NOT NULL,
              payload_cipher BLOB NOT NULL,
              payload_tag BLOB NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_anonymous_visual_evidence_session
              ON anonymous_visual_evidence(session_id);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> SaveAnonymousVisualEvidenceAsync(
        AnonymousVisualEvidenceInterval evidence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var payload = new AnonymousVisualEvidencePayload(
            AnonymousVisualEvidenceVersions.CurrentPayload,
            evidence.Kind,
            evidence.Availability,
            evidence.Start.Ticks,
            evidence.End.Ticks,
            evidence.Confidence,
            (int)evidence.Provenance.Provider,
            evidence.Provenance.ProfileVersion,
            evidence.Provenance.EvidenceVersion,
            evidence.Provenance.DetectorVersion,
            evidence.Provenance.PolicyVersion);
        var encrypted = protector.Protect(
            JsonSerializer.SerializeToUtf8Bytes(payload),
            VisualEvidenceAssociatedData(evidence.SessionId, evidence.Id));

        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO anonymous_visual_evidence(
              id,session_id,payload_nonce,payload_cipher,payload_tag)
            VALUES($id,$session,$n,$c,$t)
            """;
        command.Parameters.AddWithValue("$id", evidence.Id.ToString("N"));
        command.Parameters.AddWithValue("$session", evidence.SessionId);
        AddPayload(command, encrypted);
        try
        {
            return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            return false;
        }
    }

    public async Task<AnonymousVisualEvidenceReadResult> GetAnonymousVisualEvidenceAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var intervals = new List<AnonymousVisualEvidenceInterval>();
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,payload_nonce,payload_cipher,payload_tag
            FROM anonymous_visual_evidence
            WHERE session_id=$session
            ORDER BY id
            """;
        command.Parameters.AddWithValue("$session", sessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            try
            {
                var id = Guid.ParseExact(reader.GetString(0), "N");
                var plaintext = protector.Unprotect(
                    ReadPayload(reader, 1),
                    VisualEvidenceAssociatedData(sessionId, id));
                var payload = JsonSerializer.Deserialize<AnonymousVisualEvidencePayload>(plaintext);
                if (payload is null) return AnonymousVisualEvidenceReadResult.Corrupted(sessionId);
                if (payload.PayloadVersion != AnonymousVisualEvidenceVersions.CurrentPayload)
                    return AnonymousVisualEvidenceReadResult.UnsupportedVersion(sessionId);

                intervals.Add(AnonymousVisualEvidenceInterval.Restore(
                    id,
                    sessionId,
                    payload.Kind,
                    payload.Availability,
                    TimeSpan.FromTicks(payload.StartTicks),
                    TimeSpan.FromTicks(payload.EndTicks),
                    payload.Confidence,
                    new(
                        (MeetingProvider)payload.Provider,
                        payload.ProfileVersion,
                        payload.EvidenceVersion,
                        payload.DetectorVersion,
                        payload.PolicyVersion)));
            }
            catch (Exception exception) when (IsUnreadableVisualEvidence(exception))
            {
                return AnonymousVisualEvidenceReadResult.Corrupted(sessionId);
            }
        }

        var ordered = intervals
            .OrderBy(interval => interval.Start)
            .ThenBy(interval => interval.End)
            .ThenBy(interval => interval.Kind)
            .ThenBy(interval => interval.Id)
            .ToArray();
        return AnonymousVisualEvidenceReadResult.Loaded(sessionId, ordered);
    }

    private static bool IsUnreadableVisualEvidence(Exception exception) =>
        exception is CryptographicException or JsonException or FormatException or InvalidCastException or ArgumentException;

    private static string VisualEvidenceAssociatedData(string sessionId, Guid id) =>
        $"anonymous-visual-evidence:{sessionId}:{id:N}:payload";

    private sealed record AnonymousVisualEvidencePayload(
        int PayloadVersion,
        AnonymousVisualEvidenceKind Kind,
        AnonymousVisualAnalysisAvailability Availability,
        long StartTicks,
        long EndTicks,
        double Confidence,
        int Provider,
        int ProfileVersion,
        int EvidenceVersion,
        int DetectorVersion,
        int PolicyVersion);
}
