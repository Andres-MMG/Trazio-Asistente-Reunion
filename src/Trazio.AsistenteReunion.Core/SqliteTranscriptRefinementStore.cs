using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Trazio.AsistenteReunion.Core;

public sealed partial class SqliteSessionStore
{
    private static async Task InitializeTranscriptRefinementSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS transcript_refinement_batches (
              id TEXT PRIMARY KEY,
              segment_id TEXT NOT NULL REFERENCES segments(id) ON DELETE CASCADE,
              session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
              source INTEGER NOT NULL,
              start_ms INTEGER NOT NULL, end_ms INTEGER NOT NULL,
              original_nonce BLOB NOT NULL, original_cipher BLOB NOT NULL, original_tag BLOB NOT NULL,
              generator_nonce BLOB NOT NULL, generator_cipher BLOB NOT NULL, generator_tag BLOB NOT NULL,
              configuration_nonce BLOB NOT NULL, configuration_cipher BLOB NOT NULL, configuration_tag BLOB NOT NULL,
              created_at TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_refinement_batches_session_segment
              ON transcript_refinement_batches(session_id,segment_id,created_at);
            CREATE TABLE IF NOT EXISTS transcript_refinement_proposals (
              id TEXT PRIMARY KEY,
              batch_id TEXT NOT NULL REFERENCES transcript_refinement_batches(id) ON DELETE CASCADE,
              ordinal INTEGER NOT NULL CHECK (ordinal IN (0,1)),
              is_ambiguous_alternative INTEGER NOT NULL CHECK (is_ambiguous_alternative IN (0,1)),
              review_status INTEGER NOT NULL CHECK (review_status IN (0,1,2)),
              text_nonce BLOB NOT NULL, text_cipher BLOB NOT NULL, text_tag BLOB NOT NULL,
              reviewer_nonce BLOB NULL, reviewer_cipher BLOB NULL, reviewer_tag BLOB NULL,
              reviewed_at TEXT NULL,
              source_correction_id TEXT NULL REFERENCES transcript_corrections(id) ON DELETE RESTRICT,
              CHECK ((review_status=1 AND source_correction_id IS NOT NULL) OR (review_status<>1 AND source_correction_id IS NULL)),
              UNIQUE(batch_id,ordinal));
            CREATE UNIQUE INDEX IF NOT EXISTS ux_refinement_accepted_per_batch
              ON transcript_refinement_proposals(batch_id) WHERE review_status=1;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await EnsureColumnAsync(connection, "transcript_refinement_batches", "start_ticks", "INTEGER NULL", cancellationToken);
        await EnsureColumnAsync(connection, "transcript_refinement_batches", "end_ticks", "INTEGER NULL", cancellationToken);
    }

    public async Task<TranscriptRefinementBatch> CreateRefinementBatchAsync(
        string segmentId,
        string generatorIdentity,
        string configurationFingerprint,
        IReadOnlyList<TranscriptRefinementDraft> drafts,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(segmentId);
        if (string.IsNullOrWhiteSpace(generatorIdentity) || generatorIdentity.Length > TranscriptRefinementPolicy.MaxModelIdentityCharacters)
            throw new ArgumentException("La identidad del generador no es válida.", nameof(generatorIdentity));
        if (configurationFingerprint is null || configurationFingerprint.Length != 64 ||
            !configurationFingerprint.All(Uri.IsHexDigit))
            throw new ArgumentException("Se requiere una huella SHA-256 de la configuración sin secretos.", nameof(configurationFingerprint));
        ArgumentNullException.ThrowIfNull(drafts);

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        string sessionId;
        AudioSourceKind source;
        TimeSpan start;
        TimeSpan end;
        string original;
        await using (var lookup = connection.CreateCommand())
        {
            lookup.Transaction = transaction;
            lookup.CommandText = """
                SELECT s.session_id,s.source,COALESCE(s.start_ticks,s.start_ms*10000),COALESCE(s.end_ticks,s.end_ms*10000),s.text_nonce,s.text_cipher,s.text_tag,m.state
                FROM segments s JOIN sessions m ON m.id=s.session_id WHERE s.id=$segment
                """;
            lookup.Parameters.AddWithValue("$segment", segmentId);
            await using var reader = await lookup.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("El segmento original no existe.");
            sessionId = reader.GetString(0);
            source = (AudioSourceKind)reader.GetInt32(1);
            start = TimeSpan.FromTicks(reader.GetInt64(2));
            end = TimeSpan.FromTicks(reader.GetInt64(3));
            var originalBytes = protector.Unprotect(ReadPayload(reader, 4), $"segment:{segmentId}:text");
            try { original = Encoding.UTF8.GetString(originalBytes); }
            finally { CryptographicOperations.ZeroMemory(originalBytes); }
            var state = (SessionState)reader.GetInt32(7);
            if (state is SessionState.Recording or SessionState.Paused)
                throw new InvalidOperationException("Detén la captura antes de analizar una transcripción.");
        }
        var validated = TranscriptRefinementPolicy.ValidateDrafts(original, drafts);
        var id = Guid.NewGuid().ToString("N");
        var proposals = validated.Select((draft, ordinal) => new TranscriptRefinementProposal(
            Guid.NewGuid().ToString("N"), id, ordinal, draft.Text, draft.IsAmbiguousAlternative,
            RefinementReviewStatus.Unreviewed, null, null, null)).ToArray();
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO transcript_refinement_batches(
                    id,segment_id,session_id,source,start_ms,end_ms,original_nonce,original_cipher,original_tag,
                    generator_nonce,generator_cipher,generator_tag,
                    configuration_nonce,configuration_cipher,configuration_tag,created_at,start_ticks,end_ticks)
                VALUES($id,$segment,$session,$source,$start,$end,$on,$oc,$ot,$gn,$gc,$gt,$cn,$cc,$ct,$created,$start_ticks,$end_ticks)
                """;
            insert.Parameters.AddWithValue("$id", id);
            insert.Parameters.AddWithValue("$segment", segmentId);
            insert.Parameters.AddWithValue("$session", sessionId);
            insert.Parameters.AddWithValue("$source", (int)source);
            insert.Parameters.AddWithValue("$start", (long)start.TotalMilliseconds);
            insert.Parameters.AddWithValue("$end", (long)end.TotalMilliseconds);
            insert.Parameters.AddWithValue("$start_ticks", start.Ticks);
            insert.Parameters.AddWithValue("$end_ticks", end.Ticks);
            AddNamedPayload(insert, "o", ProtectRefinementValue(original, $"refinement:{id}:original"));
            AddNamedPayload(insert, "g", ProtectRefinementValue(generatorIdentity.Trim(), $"refinement:{id}:generator"));
            AddNamedPayload(insert, "c", ProtectRefinementValue(configurationFingerprint.ToLowerInvariant(), $"refinement:{id}:configuration"));
            insert.Parameters.AddWithValue("$created", createdAt.ToString("O"));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var proposal in proposals)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO transcript_refinement_proposals(
                    id,batch_id,ordinal,is_ambiguous_alternative,review_status,text_nonce,text_cipher,text_tag)
                VALUES($id,$batch,$ordinal,$alternative,$status,$tn,$tc,$tt)
                """;
            insert.Parameters.AddWithValue("$id", proposal.Id);
            insert.Parameters.AddWithValue("$batch", id);
            insert.Parameters.AddWithValue("$ordinal", proposal.Ordinal);
            insert.Parameters.AddWithValue("$alternative", proposal.IsAmbiguousAlternative ? 1 : 0);
            insert.Parameters.AddWithValue("$status", (int)proposal.ReviewStatus);
            AddNamedPayload(insert, "t", ProtectRefinementValue(proposal.Text, $"refinement-proposal:{proposal.Id}:text"));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return new(id, segmentId, sessionId, source, start, end, original, generatorIdentity.Trim(), configurationFingerprint.ToLowerInvariant(), createdAt, proposals);
    }

    public async Task<IReadOnlyList<TranscriptRefinementBatch>> ListRefinementBatchesAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var batches = new List<TranscriptRefinementBatch>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,segment_id,source,COALESCE(start_ticks,start_ms*10000),COALESCE(end_ticks,end_ms*10000),original_nonce,original_cipher,original_tag,
                   generator_nonce,generator_cipher,generator_tag,
                   configuration_nonce,configuration_cipher,configuration_tag,created_at
            FROM transcript_refinement_batches WHERE session_id=$session ORDER BY created_at,id
            """;
        command.Parameters.AddWithValue("$session", sessionId);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetString(0);
                batches.Add(new(id, reader.GetString(1), sessionId, (AudioSourceKind)reader.GetInt32(2),
                    TimeSpan.FromTicks(reader.GetInt64(3)), TimeSpan.FromTicks(reader.GetInt64(4)),
                    ReadRefinementValue(reader, 5, $"refinement:{id}:original"),
                    ReadRefinementValue(reader, 8, $"refinement:{id}:generator"),
                    ReadRefinementValue(reader, 11, $"refinement:{id}:configuration"),
                    DateTimeOffset.Parse(reader.GetString(14)), []));
            }
        }
        var result = new List<TranscriptRefinementBatch>(batches.Count);
        foreach (var batch in batches)
        {
            var proposals = new List<TranscriptRefinementProposal>(2);
            await using var lookup = connection.CreateCommand();
            lookup.CommandText = """
                SELECT id,ordinal,is_ambiguous_alternative,review_status,text_nonce,text_cipher,text_tag,
                       reviewer_nonce,reviewer_cipher,reviewer_tag,reviewed_at,source_correction_id
                FROM transcript_refinement_proposals WHERE batch_id=$batch ORDER BY ordinal
                """;
            lookup.Parameters.AddWithValue("$batch", batch.Id);
            await using var reader = await lookup.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetString(0);
                proposals.Add(new(id, batch.Id, reader.GetInt32(1),
                    ReadRefinementValue(reader, 4, $"refinement-proposal:{id}:text"),
                    reader.GetInt32(2) != 0, (RefinementReviewStatus)reader.GetInt32(3),
                    reader.IsDBNull(7) ? null : ReadRefinementValue(reader, 7, $"refinement-proposal:{id}:reviewer"),
                    reader.IsDBNull(10) ? null : DateTimeOffset.Parse(reader.GetString(10)),
                    reader.IsDBNull(11) ? null : reader.GetString(11)));
            }
            result.Add(batch with { Proposals = proposals });
        }
        return result;
    }

    public async Task<TranscriptRefinementProposal> ReviewRefinementProposalAsync(
        string proposalId,
        RefinementReviewStatus decision,
        string reviewerName,
        DateTimeOffset reviewedAt,
        string? sourceCorrectionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(proposalId);
        if (decision is not (RefinementReviewStatus.Accepted or RefinementReviewStatus.Rejected))
            throw new ArgumentException("Se requiere aceptar o rechazar explícitamente.", nameof(decision));
        if (decision == RefinementReviewStatus.Accepted && string.IsNullOrWhiteSpace(sourceCorrectionId) ||
            decision == RefinementReviewStatus.Rejected && sourceCorrectionId is not null)
            throw new ArgumentException("Aceptar exige una corrección humana guardada; rechazar no admite corrección.", nameof(sourceCorrectionId));
        if (string.IsNullOrWhiteSpace(reviewerName) || reviewerName.Length > TranscriptRefinementPolicy.MaxReviewerCharacters)
            throw new ArgumentException("Se requiere un revisor identificado.", nameof(reviewerName));
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        string batchId;
        int ordinal;
        bool alternative;
        RefinementReviewStatus current;
        string text;
        await using (var lookup = connection.CreateCommand())
        {
            lookup.Transaction = transaction;
            lookup.CommandText = """
                SELECT batch_id,ordinal,is_ambiguous_alternative,review_status,text_nonce,text_cipher,text_tag
                FROM transcript_refinement_proposals WHERE id=$id
                """;
            lookup.Parameters.AddWithValue("$id", proposalId);
            await using var reader = await lookup.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("La propuesta ya no existe.");
            batchId = reader.GetString(0);
            ordinal = reader.GetInt32(1);
            alternative = reader.GetInt32(2) != 0;
            current = (RefinementReviewStatus)reader.GetInt32(3);
            text = ReadRefinementValue(reader, 4, $"refinement-proposal:{proposalId}:text");
        }
        if (current != RefinementReviewStatus.Unreviewed)
            throw new InvalidOperationException("La propuesta ya fue revisada.");
        if (decision == RefinementReviewStatus.Accepted)
            throw new InvalidOperationException("Usa la aceptación atómica para guardar la corrección y la decisión juntas.");
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE transcript_refinement_proposals
            SET review_status=$status,reviewer_nonce=$rn,reviewer_cipher=$rc,reviewer_tag=$rt,reviewed_at=$at,source_correction_id=$correction
            WHERE id=$id AND review_status=$unreviewed
            """;
        update.Parameters.AddWithValue("$id", proposalId);
        update.Parameters.AddWithValue("$status", (int)decision);
        update.Parameters.AddWithValue("$unreviewed", (int)RefinementReviewStatus.Unreviewed);
        update.Parameters.AddWithValue("$at", reviewedAt.ToString("O"));
        update.Parameters.AddWithValue("$correction", (object?)sourceCorrectionId ?? DBNull.Value);
        AddNamedPayload(update, "r", ProtectRefinementValue(reviewerName.Trim(), $"refinement-proposal:{proposalId}:reviewer"));
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("La propuesta cambió durante la revisión.");
        await transaction.CommitAsync(cancellationToken);
        return new(proposalId, batchId, ordinal, text, alternative, decision, reviewerName.Trim(), reviewedAt, sourceCorrectionId);
    }

    public async Task<TranscriptRefinementProposal> AcceptRefinementProposalAsync(
        string proposalId,
        int expectedSegmentRevision,
        string reviewerName,
        DateTimeOffset reviewedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(proposalId);
        if (expectedSegmentRevision < 0)
            throw new ArgumentOutOfRangeException(nameof(expectedSegmentRevision));
        if (string.IsNullOrWhiteSpace(reviewerName) || reviewerName.Length > TranscriptRefinementPolicy.MaxReviewerCharacters)
            throw new ArgumentException("Se requiere un revisor identificado.", nameof(reviewerName));
        cancellationToken.ThrowIfCancellationRequested();
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        string batchId;
        string segmentId;
        string sessionId;
        string proposalText;
        int ordinal;
        bool alternative;
        await using (var lookup = connection.CreateCommand())
        {
            lookup.Transaction = transaction;
            lookup.CommandText = """
                SELECT p.batch_id,p.ordinal,p.is_ambiguous_alternative,p.review_status,
                       p.text_nonce,p.text_cipher,p.text_tag,
                       b.segment_id,b.session_id,b.original_nonce,b.original_cipher,b.original_tag,
                       s.text_nonce,s.text_cipher,s.text_tag,m.state
                FROM transcript_refinement_proposals p
                JOIN transcript_refinement_batches b ON b.id=p.batch_id
                JOIN segments s ON s.id=b.segment_id
                JOIN sessions m ON m.id=b.session_id
                WHERE p.id=$id
                """;
            lookup.Parameters.AddWithValue("$id", proposalId);
            await using var reader = await lookup.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("La propuesta ya no existe.");
            batchId = reader.GetString(0);
            ordinal = reader.GetInt32(1);
            alternative = reader.GetInt32(2) != 0;
            if ((RefinementReviewStatus)reader.GetInt32(3) != RefinementReviewStatus.Unreviewed)
                throw new InvalidOperationException("La propuesta ya fue revisada.");
            proposalText = ReadRefinementValue(reader, 4, $"refinement-proposal:{proposalId}:text");
            segmentId = reader.GetString(7);
            sessionId = reader.GetString(8);
            var originalSnapshot = ReadRefinementValue(reader, 9, $"refinement:{batchId}:original");
            var originalNow = ReadRefinementValue(reader, 12, $"segment:{segmentId}:text");
            if (!string.Equals(originalSnapshot, originalNow, StringComparison.Ordinal))
                throw new InvalidOperationException("El segmento original cambió desde la propuesta.");
            if ((SessionState)reader.GetInt32(15) is SessionState.Recording or SessionState.Paused)
                throw new InvalidOperationException("Detén la captura antes de aceptar una propuesta.");
        }
        await using (var approved = connection.CreateCommand())
        {
            approved.Transaction = transaction;
            approved.CommandText = """
                SELECT action FROM segment_review_decisions
                WHERE segment_id=$segment ORDER BY revision DESC LIMIT 1
                """;
            approved.Parameters.AddWithValue("$segment", segmentId);
            var action = await approved.ExecuteScalarAsync(cancellationToken);
            if (action is not null && (SegmentReviewDecisionAction)Convert.ToInt32(action) == SegmentReviewDecisionAction.ApproveOriginal)
                throw new InvalidOperationException("El original ya fue aprobado por una persona.");
        }
        await using (var prior = connection.CreateCommand())
        {
            prior.Transaction = transaction;
            prior.CommandText = """
                SELECT revision,action FROM transcript_corrections
                WHERE segment_id=$segment ORDER BY revision DESC LIMIT 1
                """;
            prior.Parameters.AddWithValue("$segment", segmentId);
            await using var reader = await prior.ExecuteReaderAsync(cancellationToken);
            var currentRevision = await reader.ReadAsync(cancellationToken) ? reader.GetInt32(0) : 0;
            if (currentRevision != expectedSegmentRevision)
                throw new InvalidOperationException("La transcripción cambió durante la revisión; vuelve a cargarla.");
            if (currentRevision > 0 && (CorrectionAction)reader.GetInt32(1) == CorrectionAction.SetText)
                throw new InvalidOperationException("El texto ya tiene una corrección humana activa.");
        }
        await using (var accepted = connection.CreateCommand())
        {
            accepted.Transaction = transaction;
            accepted.CommandText = """
                SELECT COUNT(*) FROM transcript_refinement_proposals
                WHERE batch_id=$batch AND review_status=$accepted
                """;
            accepted.Parameters.AddWithValue("$batch", batchId);
            accepted.Parameters.AddWithValue("$accepted", (int)RefinementReviewStatus.Accepted);
            if (Convert.ToInt64(await accepted.ExecuteScalarAsync(cancellationToken)) != 0)
                throw new InvalidOperationException("Este análisis ya tiene una propuesta aceptada.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        var correctionId = Guid.NewGuid().ToString("N");
        var reviewer = reviewerName.Trim();
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO transcript_corrections(
                  id,segment_id,session_id,revision,action,corrected_nonce,corrected_cipher,corrected_tag,
                  editor_nonce,editor_cipher,editor_tag,created_at)
                VALUES($id,$segment,$session,$revision,$action,$tn,$tc,$tt,$en,$ec,$et,$created)
                """;
            insert.Parameters.AddWithValue("$id", correctionId);
            insert.Parameters.AddWithValue("$segment", segmentId);
            insert.Parameters.AddWithValue("$session", sessionId);
            insert.Parameters.AddWithValue("$revision", expectedSegmentRevision + 1);
            insert.Parameters.AddWithValue("$action", (int)CorrectionAction.SetText);
            AddNamedPayload(insert, "t", ProtectRefinementValue(proposalText, $"correction:{correctionId}:text"));
            AddNamedPayload(insert, "e", ProtectRefinementValue(reviewer, $"correction:{correctionId}:editor"));
            insert.Parameters.AddWithValue("$created", reviewedAt.ToString("O"));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE transcript_refinement_proposals
                SET review_status=$status,reviewer_nonce=$rn,reviewer_cipher=$rc,reviewer_tag=$rt,
                    reviewed_at=$at,source_correction_id=$correction
                WHERE id=$id AND review_status=$unreviewed
                """;
            update.Parameters.AddWithValue("$id", proposalId);
            update.Parameters.AddWithValue("$status", (int)RefinementReviewStatus.Accepted);
            update.Parameters.AddWithValue("$unreviewed", (int)RefinementReviewStatus.Unreviewed);
            update.Parameters.AddWithValue("$at", reviewedAt.ToString("O"));
            update.Parameters.AddWithValue("$correction", correctionId);
            AddNamedPayload(update, "r", ProtectRefinementValue(reviewer, $"refinement-proposal:{proposalId}:reviewer"));
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("La propuesta cambió durante la revisión.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(cancellationToken);
        return new(proposalId, batchId, ordinal, proposalText, alternative,
            RefinementReviewStatus.Accepted, reviewer, reviewedAt, correctionId);
    }
    private EncryptedPayload ProtectRefinementValue(string value, string purpose)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        try { return protector.Protect(bytes, purpose); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private string ReadRefinementValue(SqliteDataReader reader, int offset, string purpose)
    {
        var bytes = protector.Unprotect(ReadPayload(reader, offset), purpose);
        try { return Encoding.UTF8.GetString(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
