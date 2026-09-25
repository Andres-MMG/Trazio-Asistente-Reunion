using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Trazio.AsistenteReunion.Core;

public sealed partial class SqliteSessionStore
{
    private static readonly JsonSerializerOptions RefinementEvaluationJsonOptions = new(JsonSerializerDefaults.Web);
    private sealed record RefinementEvaluationMetadata(RefinementEvaluatorIdentity? Evaluator, string PolicyVersion);

    private static async Task InitializeTranscriptRefinementEvaluationSchemaAsync(
        SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS transcript_refinement_evaluations (
              id TEXT PRIMARY KEY,
              batch_id TEXT NOT NULL REFERENCES transcript_refinement_batches(id) ON DELETE CASCADE,
              source INTEGER NOT NULL,
              recommendation INTEGER NOT NULL CHECK (recommendation IN (0,1,2)),
              recommended_proposal_id TEXT NULL,
              snapshot_nonce BLOB NOT NULL, snapshot_cipher BLOB NOT NULL, snapshot_tag BLOB NOT NULL,
              metadata_nonce BLOB NOT NULL, metadata_cipher BLOB NOT NULL, metadata_tag BLOB NOT NULL,
              judgment_nonce BLOB NULL, judgment_cipher BLOB NULL, judgment_tag BLOB NULL,
              created_at TEXT NOT NULL,
              CHECK ((judgment_nonce IS NULL AND judgment_cipher IS NULL AND judgment_tag IS NULL) OR
                     (judgment_nonce IS NOT NULL AND judgment_cipher IS NOT NULL AND judgment_tag IS NOT NULL)));
            CREATE INDEX IF NOT EXISTS ix_refinement_evaluations_batch_created
              ON transcript_refinement_evaluations(batch_id,created_at);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public Task<TranscriptRefinementEvaluation> SaveTranscriptRefinementEvaluationAsync(
        string batchId, RefinementEvaluatorIdentity? evaluator,
        RefinementEvaluationJudgment? judgment, DateTimeOffset createdAt,
        CancellationToken cancellationToken = default) =>
        SaveEvaluationCoreAsync(batchId, null, null, evaluator, judgment, createdAt, cancellationToken);

    public Task<TranscriptRefinementEvaluation> SaveJointRefinementEvaluationAsync(
        string batchId, string qwenRevisionId, RefinementEvaluationSnapshot expectedSnapshot,
        RefinementEvaluatorIdentity evaluator, RefinementEvaluationJudgment judgment,
        DateTimeOffset createdAt, CancellationToken cancellationToken = default) =>
        SaveEvaluationCoreAsync(batchId, qwenRevisionId, expectedSnapshot, evaluator, judgment, createdAt, cancellationToken);

    public async Task<RefinementEvaluationSnapshot> BuildJointRefinementSnapshotAsync(
        string batchId, string qwenRevisionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
        ArgumentException.ThrowIfNullOrWhiteSpace(qwenRevisionId);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var snapshot = await ReadEvaluationSnapshotAsync(connection, transaction, batchId, qwenRevisionId, cancellationToken);
        TranscriptRefinementEvaluationPolicy.ValidateJointSnapshot(snapshot);
        return snapshot;
    }

    private async Task<TranscriptRefinementEvaluation> SaveEvaluationCoreAsync(
        string batchId, string? qwenRevisionId, RefinementEvaluationSnapshot? expectedSnapshot,
        RefinementEvaluatorIdentity? evaluator, RefinementEvaluationJudgment? judgment,
        DateTimeOffset createdAt, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
        cancellationToken.ThrowIfCancellationRequested();
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var snapshot = await ReadEvaluationSnapshotAsync(
            connection, transaction, batchId, qwenRevisionId, cancellationToken);
        var policyVersion = qwenRevisionId is null
            ? TranscriptRefinementEvaluationPolicy.Version1
            : TranscriptRefinementEvaluationPolicy.Version2;
        if (expectedSnapshot is not null &&
            JsonSerializer.Serialize(expectedSnapshot, RefinementEvaluationJsonOptions) !=
            JsonSerializer.Serialize(snapshot, RefinementEvaluationJsonOptions))
            throw new InvalidOperationException("El segmento, corrección o modelo cambió durante la evaluación.");
        if (qwenRevisionId is not null &&
            (evaluator?.Model != "laya" || evaluator.QuestionVersion != "refinement-laya-questions-v2"))
            throw new ArgumentException("La comparación conjunta requiere Laya local v2.", nameof(evaluator));

        TranscriptRefinementEvaluationPolicy.ValidateEvaluator(evaluator, snapshot.Proposals.Count > 0 || snapshot.QwenAsr is not null);
        var (recommendation, recommendedProposalId) = TranscriptRefinementEvaluationPolicy.DecideForVersion(policyVersion, snapshot, judgment);
        var id = Guid.NewGuid().ToString("N");
        var snapshotJson = JsonSerializer.Serialize(snapshot, RefinementEvaluationJsonOptions);
        var metadataJson = JsonSerializer.Serialize(
            new RefinementEvaluationMetadata(evaluator, policyVersion),
            RefinementEvaluationJsonOptions);
        var judgmentJson = judgment is null ? null : JsonSerializer.Serialize(judgment, RefinementEvaluationJsonOptions);
        if (Encoding.UTF8.GetByteCount(snapshotJson) > 12_000 ||
            Encoding.UTF8.GetByteCount(metadataJson) > 1_000 ||
            (judgmentJson is not null && Encoding.UTF8.GetByteCount(judgmentJson) > 8_000))
            throw new ArgumentException("La evaluación supera los límites de almacenamiento.", nameof(judgment));
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO transcript_refinement_evaluations(
                  id,batch_id,source,recommendation,recommended_proposal_id,
                  snapshot_nonce,snapshot_cipher,snapshot_tag,
                  metadata_nonce,metadata_cipher,metadata_tag,
                  judgment_nonce,judgment_cipher,judgment_tag,created_at)
                VALUES($id,$batch,$source,$recommendation,$proposal,
                  $sn,$sc,$st,$mn,$mc,$mt,$jn,$jc,$jt,$created)
                """;
            insert.Parameters.AddWithValue("$id", id);
            insert.Parameters.AddWithValue("$batch", batchId);
            insert.Parameters.AddWithValue("$source", (int)snapshot.Source);
            insert.Parameters.AddWithValue("$recommendation", (int)recommendation);
            insert.Parameters.AddWithValue("$proposal", (object?)recommendedProposalId ?? DBNull.Value);
            AddNamedPayload(insert, "s", ProtectRefinementValue(snapshotJson, $"refinement-evaluation:{id}:snapshot"));
            AddNamedPayload(insert, "m", ProtectRefinementValue(metadataJson, $"refinement-evaluation:{id}:metadata"));
            if (judgmentJson is null)
            {
                insert.Parameters.AddWithValue("$jn", DBNull.Value);
                insert.Parameters.AddWithValue("$jc", DBNull.Value);
                insert.Parameters.AddWithValue("$jt", DBNull.Value);
            }
            else AddNamedPayload(insert, "j", ProtectRefinementValue(judgmentJson, $"refinement-evaluation:{id}:judgment"));
            insert.Parameters.AddWithValue("$created", createdAt.ToString("O"));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(cancellationToken);
        return new(id, snapshot, evaluator, policyVersion,
            judgment, recommendation, recommendedProposalId, createdAt);
    }

    private async Task<RefinementEvaluationSnapshot> ReadEvaluationSnapshotAsync(
        SqliteConnection connection, SqliteTransaction transaction, string batchId,
        string? qwenRevisionId, CancellationToken cancellationToken)
    {
        RefinementEvaluationSnapshot snapshot;
        string segmentId;
        await using (var lookup = connection.CreateCommand())
        {
            lookup.Transaction = transaction;
            lookup.CommandText = """
                SELECT b.segment_id,b.session_id,b.source,COALESCE(b.start_ticks,b.start_ms*10000),COALESCE(b.end_ticks,b.end_ms*10000),
                       b.original_nonce,b.original_cipher,b.original_tag,
                       s.source,COALESCE(s.start_ticks,s.start_ms*10000),COALESCE(s.end_ticks,s.end_ms*10000),s.text_nonce,s.text_cipher,s.text_tag,m.state
                FROM transcript_refinement_batches b
                JOIN segments s ON s.id=b.segment_id
                JOIN sessions m ON m.id=b.session_id
                WHERE b.id=$batch AND s.session_id=b.session_id
                """;
            lookup.Parameters.AddWithValue("$batch", batchId);
            await using var reader = await lookup.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("El lote de refinamiento ya no existe.");
            segmentId = reader.GetString(0);
            var source = (AudioSourceKind)reader.GetInt32(2);
            var start = TimeSpan.FromTicks(reader.GetInt64(3));
            var end = TimeSpan.FromTicks(reader.GetInt64(4));
            var original = ReadRefinementValue(reader, 5, $"refinement:{batchId}:original");
            var segmentOriginal = ReadRefinementValue(reader, 11, $"segment:{segmentId}:text");
            if (source != (AudioSourceKind)reader.GetInt32(8) ||
                start != TimeSpan.FromTicks(reader.GetInt64(9)) ||
                end != TimeSpan.FromTicks(reader.GetInt64(10)) ||
                !string.Equals(original, segmentOriginal, StringComparison.Ordinal))
                throw new InvalidOperationException("El lote no coincide con el segmento original.");
            if ((SessionState)reader.GetInt32(14) is SessionState.Recording or SessionState.Paused)
                throw new InvalidOperationException("Detén la captura antes de evaluar propuestas.");
            snapshot = new(batchId, reader.GetString(1), source, start, end, original, []);
        }
        var proposals = new List<RefinementEvaluationCandidate>(2);
        await using (var lookup = connection.CreateCommand())
        {
            lookup.Transaction = transaction;
            lookup.CommandText = """
                SELECT id,text_nonce,text_cipher,text_tag FROM transcript_refinement_proposals
                WHERE batch_id=$batch ORDER BY ordinal
                """;
            lookup.Parameters.AddWithValue("$batch", batchId);
            await using var reader = await lookup.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var proposalId = reader.GetString(0);
                proposals.Add(new(proposalId, ReadRefinementValue(reader, 1, $"refinement-proposal:{proposalId}:text")));
            }
        }
        snapshot = snapshot with { Proposals = proposals.ToArray() };
        if (qwenRevisionId is not null)
        {
            snapshot = snapshot with
            {
                QwenAsr = await ReadQwenCandidateAsync(connection, transaction, qwenRevisionId,
                    segmentId, snapshot, cancellationToken),
                CorrectionRevision = await ReadCorrectionRevisionAsync(connection, transaction,
                    segmentId, cancellationToken)
            };
        }
        return snapshot;
    }

    private static async Task<int> ReadCorrectionRevisionAsync(
        SqliteConnection connection, SqliteTransaction transaction, string segmentId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(MAX(revision),0) FROM transcript_corrections WHERE segment_id=$segment";
        command.Parameters.AddWithValue("$segment", segmentId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private async Task<RefinementObservedAsrCandidate> ReadQwenCandidateAsync(
        SqliteConnection connection, SqliteTransaction transaction, string revisionId,
        string segmentId, RefinementEvaluationSnapshot snapshot, CancellationToken cancellationToken)
    {
        string model;
        string hash;
        string language;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT session_id,source,status,model_nonce,model_cipher,model_tag,
                       model_hash_nonce,model_hash_cipher,model_hash_tag,language,
                       scope_segment_id,COALESCE(scope_start_ticks,scope_start_ms*10000),
                       COALESCE(scope_end_ticks,scope_end_ms*10000),ended_at,error_nonce,started_at,producer_identity
                FROM transcript_model_revisions WHERE id=$id
                """;
            command.Parameters.AddWithValue("$id", revisionId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken) || reader.GetString(0) != snapshot.SessionId ||
                (AudioSourceKind)reader.GetInt32(1) != snapshot.Source ||
                (ModelRevisionStatus)reader.GetInt32(2) != ModelRevisionStatus.Succeeded ||
                reader.IsDBNull(10) || reader.GetString(10) != segmentId ||
                reader.IsDBNull(11) || reader.GetInt64(11) != snapshot.Start.Ticks ||
                reader.IsDBNull(12) || reader.GetInt64(12) != snapshot.End.Ticks ||
                reader.IsDBNull(13) || !reader.IsDBNull(14) || reader.IsDBNull(6) ||
                reader.IsDBNull(16) || reader.GetString(16) != ModelRevisionProducer.Qwen3AsrLlamaCppV1 ||
                DateTimeOffset.Parse(reader.GetString(15)) > DateTimeOffset.UtcNow.AddMinutes(5) ||
                DateTimeOffset.Parse(reader.GetString(13)) > DateTimeOffset.UtcNow.AddMinutes(5) ||
                DateTimeOffset.Parse(reader.GetString(13)) < DateTimeOffset.Parse(reader.GetString(15)))
                throw new InvalidOperationException("La segunda transcripción no corresponde exactamente al fragmento original.");
            model = UnprotectRevisionRequired(reader, 3, $"model-revision:{revisionId}:model");
            hash = UnprotectRevisionRequired(reader, 6, $"model-revision:{revisionId}:hash");
            language = reader.GetString(9);
        }
        var texts = new List<string>();
        TimeSpan previousEnd = snapshot.Start;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT sequence,COALESCE(start_ticks,start_ms*10000),
                       COALESCE(end_ticks,end_ms*10000),id,text_nonce,text_cipher,text_tag
                FROM transcript_model_revision_segments WHERE revision_id=$id ORDER BY sequence
                """;
            command.Parameters.AddWithValue("$id", revisionId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var start = TimeSpan.FromTicks(reader.GetInt64(1));
                var end = TimeSpan.FromTicks(reader.GetInt64(2));
                if (reader.GetInt64(0) != texts.Count || start < previousEnd ||
                    end <= start || end > snapshot.End || texts.Count >= 64)
                    throw new InvalidOperationException("La segunda transcripción contiene tiempos fuera del fragmento.");
                var id = reader.GetString(3);
                texts.Add(UnprotectRevisionRequired(reader, 4, $"model-revision-segment:{id}:text").Trim());
                previousEnd = end;
            }
        }
        if (texts.Count == 0 || texts.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException("La segunda transcripción no contiene texto válido.");
        var text = string.Join(' ', texts);
        TranscriptRefinementPolicy.ValidateText(text, nameof(revisionId));
        return new(revisionId, segmentId, TranscriptRefinementEvaluationPolicy.QwenChoiceId(revisionId),
            text, model, hash, language, snapshot.Source, snapshot.Start, snapshot.End,
            ModelRevisionProducer.Qwen3AsrLlamaCppV1);
    }

    public async Task<IReadOnlyList<TranscriptRefinementEvaluation>> ListTranscriptRefinementEvaluationsAsync(
        string batchId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
        var result = new List<TranscriptRefinementEvaluation>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,source,recommendation,recommended_proposal_id,
                   snapshot_nonce,snapshot_cipher,snapshot_tag,
                   metadata_nonce,metadata_cipher,metadata_tag,
                   judgment_nonce,judgment_cipher,judgment_tag,created_at
            FROM transcript_refinement_evaluations WHERE batch_id=$batch ORDER BY created_at,id
            """;
        command.Parameters.AddWithValue("$batch", batchId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetString(0);
            try
            {
                var snapshot = JsonSerializer.Deserialize<RefinementEvaluationSnapshot>(
                    ReadRefinementValue(reader, 4, $"refinement-evaluation:{id}:snapshot"),
                    RefinementEvaluationJsonOptions) ?? throw new InvalidDataException("La evaluación guardada no es válida.");
                var metadata = JsonSerializer.Deserialize<RefinementEvaluationMetadata>(
                    ReadRefinementValue(reader, 7, $"refinement-evaluation:{id}:metadata"),
                    RefinementEvaluationJsonOptions) ?? throw new InvalidDataException("La evaluación guardada no es válida.");
                var judgment = reader.IsDBNull(10) ? null : JsonSerializer.Deserialize<RefinementEvaluationJudgment>(
                    ReadRefinementValue(reader, 10, $"refinement-evaluation:{id}:judgment"),
                    RefinementEvaluationJsonOptions);
                if (snapshot.BatchId != batchId || snapshot.Source != (AudioSourceKind)reader.GetInt32(1) ||
                    (reader.IsDBNull(10) != (judgment is null)))
                    throw new InvalidDataException("La evaluación guardada no es válida.");
                TranscriptRefinementEvaluationPolicy.ValidateEvaluator(metadata.Evaluator,
                    snapshot.Proposals.Count > 0 || snapshot.QwenAsr is not null);
                var storedRecommendation = (RefinementEvaluationRecommendation)reader.GetInt32(2);
                var storedProposalId = reader.IsDBNull(3) ? null : reader.GetString(3);
                var expected = TranscriptRefinementEvaluationPolicy.DecideForVersion(
                    metadata.PolicyVersion, snapshot, judgment);
                if (expected.Recommendation != storedRecommendation || expected.ProposalId != storedProposalId)
                    throw new InvalidDataException("La evaluación guardada no es válida.");
                result.Add(new(id, snapshot, metadata.Evaluator, metadata.PolicyVersion, judgment,
                    storedRecommendation, storedProposalId, DateTimeOffset.Parse(reader.GetString(13))));
            }
            catch (JsonException)
            {
                throw new InvalidDataException("La evaluación guardada no es válida.");
            }
        }
        return result;
    }
}
