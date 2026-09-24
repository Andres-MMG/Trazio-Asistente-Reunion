using System.Text;
using Microsoft.Data.Sqlite;

namespace Trazio.AsistenteReunion.Core;

public sealed partial class SqliteSessionStore
{
    public async Task<HistorySearchResult> SearchHistoryAsync(
        string query,
        int maximumResults = HistorySearchText.MaximumResults,
        CancellationToken cancellationToken = default)
    {
        var preparedQuery = HistorySearchText.PrepareQuery(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumResults, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumResults, HistorySearchText.MaximumResults);
        var normalizedQuery = HistorySearchText.Normalize(preparedQuery);
        var hits = new List<HistorySearchHit>(Math.Min(maximumResults, 32));
        var matchCount = 0;
        string? activeSessionId = null;
        string? activeSessionTitle = null;
        DateTimeOffset activeSessionStartedAt = default;

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
              s.id,s.title_nonce,s.title_cipher,s.title_tag,s.started_at,
              g.id,g.source,g.sequence,g.start_ms,g.end_ms,
              g.text_nonce,g.text_cipher,g.text_tag,
              c.id,c.action,c.corrected_nonce,c.corrected_cipher,c.corrected_tag
            FROM sessions s
            LEFT JOIN segments g ON g.session_id=s.id
            LEFT JOIN transcript_corrections c ON c.id=(
              SELECT c2.id FROM transcript_corrections c2
              WHERE c2.segment_id=g.id
              ORDER BY c2.revision DESC
              LIMIT 1)
            ORDER BY s.started_at DESC,s.id,g.start_ms,g.source,g.sequence,g.id
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sessionId = reader.GetString(0);
            if (!string.Equals(activeSessionId, sessionId, StringComparison.Ordinal))
            {
                activeSessionId = sessionId;
                activeSessionTitle = UnprotectSearchValue(reader, 1, $"session:{sessionId}:title");
                activeSessionStartedAt = DateTimeOffset.Parse(reader.GetString(4));
                if (HistorySearchText.Contains(activeSessionTitle, normalizedQuery))
                {
                    AddHit(new(
                        sessionId,
                        activeSessionTitle,
                        activeSessionStartedAt,
                        null,
                        null,
                        null,
                        HistorySearchText.CreateSnippet(activeSessionTitle, normalizedQuery),
                        HistorySearchMatchKind.SessionTitle));
                }
            }

            if (reader.IsDBNull(5)) continue;
            var segmentId = reader.GetString(5);
            var sourceValue = reader.GetInt32(6);
            if (!Enum.IsDefined(typeof(AudioSourceKind), sourceValue))
                throw new InvalidDataException("La fuente de un segmento guardado no es válida.");
            var source = (AudioSourceKind)sourceValue;
            var originalText = UnprotectSearchValue(reader, 10, $"segment:{segmentId}:text");
            var effectiveText = originalText;
            if (!reader.IsDBNull(13))
            {
                var correctionId = reader.GetString(13);
                var actionValue = reader.GetInt32(14);
                if (!Enum.IsDefined(typeof(CorrectionAction), actionValue))
                    throw new InvalidDataException("La acción de una corrección guardada no es válida.");
                if ((CorrectionAction)actionValue == CorrectionAction.SetText)
                    effectiveText = UnprotectSearchValue(reader, 15, $"correction:{correctionId}:text");
            }

            if (!HistorySearchText.Contains(effectiveText, normalizedQuery)) continue;
            AddHit(new(
                sessionId,
                activeSessionTitle!,
                activeSessionStartedAt,
                segmentId,
                source,
                TimeSpan.FromMilliseconds(reader.GetInt64(8)),
                HistorySearchText.CreateSnippet(effectiveText, normalizedQuery),
                HistorySearchMatchKind.TranscriptSegment));
        }

        return new(hits, matchCount, matchCount > hits.Count);

        void AddHit(HistorySearchHit hit)
        {
            matchCount++;
            if (hits.Count < maximumResults) hits.Add(hit);
        }
    }

    private string UnprotectSearchValue(SqliteDataReader reader, int offset, string purpose)
    {
        if (reader.IsDBNull(offset) || reader.IsDBNull(offset + 1) || reader.IsDBNull(offset + 2))
            throw new InvalidDataException("Un valor cifrado requerido está incompleto.");
        return Encoding.UTF8.GetString(protector.Unprotect(ReadPayload(reader, offset), purpose));
    }
}
