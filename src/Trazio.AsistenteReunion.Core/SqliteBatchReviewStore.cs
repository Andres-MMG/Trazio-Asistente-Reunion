using System.Text;
using Microsoft.Data.Sqlite;

namespace Trazio.AsistenteReunion.Core;

public sealed partial class SqliteSessionStore
{
    private static async Task InitializeBatchReviewSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS segment_review_decisions (
              id TEXT PRIMARY KEY,
              segment_id TEXT NOT NULL REFERENCES segments(id) ON DELETE CASCADE,
              session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
              revision INTEGER NOT NULL CHECK(revision > 0),
              action INTEGER NOT NULL CHECK(action IN (0, 1)),
              reviewer_nonce BLOB NOT NULL,
              reviewer_cipher BLOB NOT NULL,
              reviewer_tag BLOB NOT NULL,
              created_at TEXT NOT NULL,
              UNIQUE(segment_id, revision));
            CREATE INDEX IF NOT EXISTS ix_segment_review_decisions_session
              ON segment_review_decisions(session_id, segment_id, revision);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<PendingSegmentReviewResult> ListPendingSegmentReviewsAsync(
        int maximumResults = PendingSegmentReviewLimits.MaximumVisibleItems,
        CancellationToken cancellationToken = default)
    {
        if (maximumResults is < 1 or > PendingSegmentReviewLimits.MaximumVisibleItems)
            throw new ArgumentOutOfRangeException(nameof(maximumResults));

        var visible = new List<PendingSegmentReview>(maximumResults);
        var totalCount = 0;
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH latest_decisions AS (
              SELECT decision.*
              FROM segment_review_decisions decision
              INNER JOIN (
                SELECT segment_id, MAX(revision) AS revision
                FROM segment_review_decisions
                GROUP BY segment_id
              ) latest
                ON latest.segment_id=decision.segment_id AND latest.revision=decision.revision
            )
            SELECT session.id,
                   session.title_nonce,session.title_cipher,session.title_tag,session.started_at,
                   segment.id,segment.source,segment.sequence,segment.start_ms,segment.end_ms,
                   segment.text_nonce,segment.text_cipher,segment.text_tag,
                   segment.speaker_nonce,segment.speaker_cipher,segment.speaker_tag,
                   latest.revision,latest.id,latest.action,
                   latest.reviewer_nonce,latest.reviewer_cipher,latest.reviewer_tag
            FROM segments segment
            INNER JOIN sessions session ON session.id=segment.session_id
            LEFT JOIN latest_decisions latest ON latest.segment_id=segment.id
            WHERE session.state IN ($completed,$interrupted)
              AND NOT EXISTS (
                SELECT 1 FROM transcript_corrections correction
                WHERE correction.segment_id=segment.id)
              AND (latest.action IS NULL OR latest.action<>$approved)
            ORDER BY session.started_at DESC,segment.start_ms,segment.source,segment.sequence,segment.id
            """;
        command.Parameters.AddWithValue("$completed", (int)SessionState.Completed);
        command.Parameters.AddWithValue("$interrupted", (int)SessionState.Interrupted);
        command.Parameters.AddWithValue("$approved", (int)SegmentReviewDecisionAction.ApproveOriginal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sessionId = reader.GetString(0);
            var segmentId = reader.GetString(5);
            var title = Encoding.UTF8.GetString(
                protector.Unprotect(ReadPayload(reader, 1), $"session:{sessionId}:title"));
            var text = Encoding.UTF8.GetString(
                protector.Unprotect(ReadPayload(reader, 10), $"segment:{segmentId}:text"));
            var source = (AudioSourceKind)reader.GetInt32(6);
            var speaker = source == AudioSourceKind.Microphone
                ? UnprotectOptional(reader, 13, $"segment:{segmentId}:speaker")
                : null;
            var decisionRevision = reader.IsDBNull(16) ? 0 : reader.GetInt32(16);
            if (!reader.IsDBNull(17))
            {
                var decisionId = reader.GetString(17);
                var action = (SegmentReviewDecisionAction)reader.GetInt32(18);
                if (action != SegmentReviewDecisionAction.Reopen)
                    throw new InvalidDataException("La bandeja contiene un estado de revisión no válido.");
                _ = Encoding.UTF8.GetString(
                    protector.Unprotect(ReadPayload(reader, 19), $"segment-review:{decisionId}:reviewer"));
            }

            totalCount++;
            if (visible.Count >= maximumResults) continue;
            visible.Add(new(
                sessionId,
                segmentId,
                title,
                DateTimeOffset.Parse(reader.GetString(4)),
                source,
                reader.GetInt64(7),
                TimeSpan.FromMilliseconds(reader.GetInt64(8)),
                TimeSpan.FromMilliseconds(reader.GetInt64(9)),
                text,
                speaker,
                decisionRevision));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new(visible, totalCount, totalCount > visible.Count);
    }

    public Task<SegmentReviewWriteResult> ApproveOriginalSegmentAsync(
        string sessionId,
        string segmentId,
        int expectedDecisionRevision,
        string reviewerName,
        CancellationToken cancellationToken = default) =>
        AppendSegmentReviewDecisionAsync(
            sessionId,
            segmentId,
            expectedDecisionRevision,
            reviewerName,
            SegmentReviewDecisionAction.ApproveOriginal,
            cancellationToken);

    public Task<SegmentReviewWriteResult> ReopenOriginalSegmentAsync(
        string sessionId,
        string segmentId,
        int expectedDecisionRevision,
        string reviewerName,
        CancellationToken cancellationToken = default) =>
        AppendSegmentReviewDecisionAsync(
            sessionId,
            segmentId,
            expectedDecisionRevision,
            reviewerName,
            SegmentReviewDecisionAction.Reopen,
            cancellationToken);

    public async Task<BatchSegmentReviewWriteResult> ApproveOriginalSegmentsAsync(
        IReadOnlyList<SegmentReviewApprovalRequest> requests,
        string reviewerName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count == 0)
            throw new ArgumentException("Selecciona al menos un segmento.", nameof(requests));
        if (requests.Count > PendingSegmentReviewLimits.MaximumVisibleItems)
            throw new ArgumentOutOfRangeException(
                nameof(requests),
                $"No se pueden aprobar más de {PendingSegmentReviewLimits.MaximumVisibleItems} segmentos a la vez.");
        if (string.IsNullOrWhiteSpace(reviewerName))
            throw new ArgumentException("Se requiere el nombre del revisor local.", nameof(reviewerName));

        foreach (var request in requests)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(request.SessionId);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.SegmentId);
            if (request.ExpectedDecisionRevision < 0)
                throw new ArgumentOutOfRangeException(nameof(requests));
        }
        if (requests.Select(request => request.SegmentId).Distinct(StringComparer.Ordinal).Count() != requests.Count)
            throw new ArgumentException("La selección contiene segmentos duplicados.", nameof(requests));

        var normalizedReviewer = reviewerName.Trim();
        var conflicts = new List<BatchSegmentReviewConflict>();
        var states = new List<(SegmentReviewApprovalRequest Request, string SessionId, int Revision)>();

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        foreach (var request in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var lookup = connection.CreateCommand();
            lookup.Transaction = transaction;
            lookup.CommandText = """
                SELECT segment.session_id,session.state,
                       EXISTS(SELECT 1 FROM transcript_corrections correction WHERE correction.segment_id=segment.id),
                       COALESCE((
                           SELECT decision.revision
                           FROM segment_review_decisions decision
                           WHERE decision.segment_id=segment.id
                           ORDER BY decision.revision DESC
                           LIMIT 1
                       ),0),
                       (
                           SELECT decision.action
                           FROM segment_review_decisions decision
                           WHERE decision.segment_id=segment.id
                           ORDER BY decision.revision DESC
                           LIMIT 1
                       )
                FROM segments segment
                INNER JOIN sessions session ON session.id=segment.session_id
                WHERE segment.id=$segment AND segment.session_id=$session
                """;
            lookup.Parameters.AddWithValue("$segment", request.SegmentId);
            lookup.Parameters.AddWithValue("$session", request.SessionId);
            await using var reader = await lookup.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                conflicts.Add(new(request, SegmentReviewWriteStatus.Missing, 0));
                continue;
            }

            var storedSessionId = reader.GetString(0);
            var sessionState = (SessionState)reader.GetInt32(1);
            var hasCorrections = reader.GetInt32(2) != 0;
            var currentRevision = reader.GetInt32(3);
            var currentAction = reader.IsDBNull(4)
                ? null
                : (SegmentReviewDecisionAction?)ReadSegmentReviewAction(reader.GetInt32(4));

            var status = sessionState is not (SessionState.Completed or SessionState.Interrupted) || hasCorrections
                ? SegmentReviewWriteStatus.StateChanged
                : currentAction == SegmentReviewDecisionAction.ApproveOriginal
                    ? SegmentReviewWriteStatus.AlreadyCurrent
                    : currentRevision != request.ExpectedDecisionRevision
                        ? SegmentReviewWriteStatus.StateChanged
                        : SegmentReviewWriteStatus.Applied;
            if (status != SegmentReviewWriteStatus.Applied)
                conflicts.Add(new(request, status, currentRevision));
            else
                states.Add((request, storedSessionId, currentRevision));
        }

        if (conflicts.Count > 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(BatchSegmentReviewWriteStatus.Conflict, [], conflicts);
        }

        var decisions = new List<SegmentReviewDecision>(states.Count);
        foreach (var state in states)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var decision = new SegmentReviewDecision(
                Guid.NewGuid().ToString("N"),
                state.Request.SegmentId,
                state.SessionId,
                state.Revision + 1,
                SegmentReviewDecisionAction.ApproveOriginal,
                normalizedReviewer,
                DateTimeOffset.UtcNow);
            var reviewer = protector.Protect(
                Encoding.UTF8.GetBytes(decision.ReviewerName),
                $"segment-review:{decision.Id}:reviewer");
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO segment_review_decisions(
                  id,segment_id,session_id,revision,action,
                  reviewer_nonce,reviewer_cipher,reviewer_tag,created_at)
                VALUES($id,$segment,$session,$revision,$action,$rn,$rc,$rt,$created)
                """;
            insert.Parameters.AddWithValue("$id", decision.Id);
            insert.Parameters.AddWithValue("$segment", decision.SegmentId);
            insert.Parameters.AddWithValue("$session", decision.SessionId);
            insert.Parameters.AddWithValue("$revision", decision.Revision);
            insert.Parameters.AddWithValue("$action", (int)decision.Action);
            insert.Parameters.AddWithValue("$rn", reviewer.Nonce);
            insert.Parameters.AddWithValue("$rc", reviewer.Ciphertext);
            insert.Parameters.AddWithValue("$rt", reviewer.Tag);
            insert.Parameters.AddWithValue("$created", decision.CreatedAt.ToString("O"));
            await insert.ExecuteNonQueryAsync(cancellationToken);
            decisions.Add(decision);
        }

        await transaction.CommitAsync(cancellationToken);
        return new(BatchSegmentReviewWriteStatus.Applied, decisions, []);
    }
    public async Task<IReadOnlyList<SegmentReviewDecision>> GetSegmentReviewDecisionsAsync(
        string segmentId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(segmentId);
        var decisions = new List<SegmentReviewDecision>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,session_id,revision,action,reviewer_nonce,reviewer_cipher,reviewer_tag,created_at
            FROM segment_review_decisions
            WHERE segment_id=$segment
            ORDER BY revision
            """;
        command.Parameters.AddWithValue("$segment", segmentId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetString(0);
            var action = ReadSegmentReviewAction(reader.GetInt32(3));
            decisions.Add(new(
                id,
                segmentId,
                reader.GetString(1),
                reader.GetInt32(2),
                action,
                Encoding.UTF8.GetString(
                    protector.Unprotect(ReadPayload(reader, 4), $"segment-review:{id}:reviewer")),
                DateTimeOffset.Parse(reader.GetString(7))));
        }
        return decisions;
    }

    private async Task<SegmentReviewWriteResult> AppendSegmentReviewDecisionAsync(
        string sessionId,
        string segmentId,
        int expectedDecisionRevision,
        string reviewerName,
        SegmentReviewDecisionAction requestedAction,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(segmentId);
        if (expectedDecisionRevision < 0)
            throw new ArgumentOutOfRangeException(nameof(expectedDecisionRevision));
        if (string.IsNullOrWhiteSpace(reviewerName))
            throw new ArgumentException("Se requiere el nombre del revisor local.", nameof(reviewerName));
        var normalizedReviewer = reviewerName.Trim();

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var segmentLookup = connection.CreateCommand();
        segmentLookup.Transaction = transaction;
        segmentLookup.CommandText = """
            SELECT segment.session_id,session.state,
                   EXISTS(SELECT 1 FROM transcript_corrections correction WHERE correction.segment_id=segment.id)
            FROM segments segment
            INNER JOIN sessions session ON session.id=segment.session_id
            WHERE segment.id=$segment AND segment.session_id=$session
            """;
        segmentLookup.Parameters.AddWithValue("$segment", segmentId);
        segmentLookup.Parameters.AddWithValue("$session", sessionId);
        await using var segmentReader = await segmentLookup.ExecuteReaderAsync(cancellationToken);
        if (!await segmentReader.ReadAsync(cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(SegmentReviewWriteStatus.Missing, 0);
        }
        var storedSessionId = segmentReader.GetString(0);
        var sessionState = (SessionState)segmentReader.GetInt32(1);
        var hasCorrections = segmentReader.GetInt32(2) != 0;
        await segmentReader.DisposeAsync();
        var currentRevision = 0;
        SegmentReviewDecisionAction? currentAction = null;
        await using (var currentLookup = connection.CreateCommand())
        {
            currentLookup.Transaction = transaction;
            currentLookup.CommandText = """
                SELECT revision,action
                FROM segment_review_decisions
                WHERE segment_id=$segment
                ORDER BY revision DESC
                LIMIT 1
                """;
            currentLookup.Parameters.AddWithValue("$segment", segmentId);
            await using var currentReader = await currentLookup.ExecuteReaderAsync(cancellationToken);
            if (await currentReader.ReadAsync(cancellationToken))
            {
                currentRevision = currentReader.GetInt32(0);
                currentAction = ReadSegmentReviewAction(currentReader.GetInt32(1));
            }
        }

        if (sessionState is not (SessionState.Completed or SessionState.Interrupted) || hasCorrections)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(SegmentReviewWriteStatus.StateChanged, currentRevision);
        }

        var alreadyCurrent = requestedAction switch
        {
            SegmentReviewDecisionAction.ApproveOriginal =>
                currentAction == SegmentReviewDecisionAction.ApproveOriginal,
            SegmentReviewDecisionAction.Reopen =>
                currentAction is null or SegmentReviewDecisionAction.Reopen,
            _ => throw new InvalidDataException("La acción de revisión no es válida.")
        };
        if (alreadyCurrent)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(SegmentReviewWriteStatus.AlreadyCurrent, currentRevision);
        }
        if (currentRevision != expectedDecisionRevision)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(SegmentReviewWriteStatus.StateChanged, currentRevision);
        }

        var decision = new SegmentReviewDecision(
            Guid.NewGuid().ToString("N"),
            segmentId,
            storedSessionId,
            currentRevision + 1,
            requestedAction,
            normalizedReviewer,
            DateTimeOffset.UtcNow);
        var reviewer = protector.Protect(
            Encoding.UTF8.GetBytes(decision.ReviewerName),
            $"segment-review:{decision.Id}:reviewer");
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO segment_review_decisions(
              id,segment_id,session_id,revision,action,
              reviewer_nonce,reviewer_cipher,reviewer_tag,created_at)
            VALUES($id,$segment,$session,$revision,$action,$rn,$rc,$rt,$created)
            """;
        insert.Parameters.AddWithValue("$id", decision.Id);
        insert.Parameters.AddWithValue("$segment", decision.SegmentId);
        insert.Parameters.AddWithValue("$session", decision.SessionId);
        insert.Parameters.AddWithValue("$revision", decision.Revision);
        insert.Parameters.AddWithValue("$action", (int)decision.Action);
        insert.Parameters.AddWithValue("$rn", reviewer.Nonce);
        insert.Parameters.AddWithValue("$rc", reviewer.Ciphertext);
        insert.Parameters.AddWithValue("$rt", reviewer.Tag);
        insert.Parameters.AddWithValue("$created", decision.CreatedAt.ToString("O"));
        await insert.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(SegmentReviewWriteStatus.Applied, decision.Revision, decision);
    }

    private static SegmentReviewDecisionAction ReadSegmentReviewAction(int raw) => raw switch
    {
        (int)SegmentReviewDecisionAction.ApproveOriginal => SegmentReviewDecisionAction.ApproveOriginal,
        (int)SegmentReviewDecisionAction.Reopen => SegmentReviewDecisionAction.Reopen,
        _ => throw new InvalidDataException("La acción de revisión almacenada no es válida.")
    };
}
