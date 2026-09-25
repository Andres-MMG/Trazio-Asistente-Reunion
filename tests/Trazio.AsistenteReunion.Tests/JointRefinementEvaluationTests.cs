using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.Tests;

public sealed class JointRefinementEvaluationTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "trazio-joint-eval-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private AesContentProtector _protector = null!;
    private SqliteSessionStore _store = null!;
    private string DatabasePath => Path.Combine(_directory, "test.db");
    private static readonly TimeSpan Start = TimeSpan.FromTicks(TimeSpan.FromSeconds(4).Ticks + 8000);
    private static readonly TimeSpan End = TimeSpan.FromTicks(TimeSpan.FromSeconds(8).Ticks + 8000);
    private static readonly string Hash = "SHA256:" + new string('A', 64) + ";MMPROJ-SHA256:" + new string('B', 64);
    private static readonly RefinementEvaluatorIdentity Evaluator = new("laya", "local-v1", "refinement-laya-questions-v2");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        _protector = new AesContentProtector(_key);
        _store = new SqliteSessionStore(DatabasePath, _protector);
        await _store.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        _protector.Dispose();
        CryptographicOperations.ZeroMemory(_key);
        Directory.Delete(_directory, true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task JointEvaluation_ZeroProposalsRetainsAcousticProvenanceAndNeverEditsOriginal()
    {
        var batch = await CreateBatchAsync([]);
        var revision = await CreateRevisionAsync(batch, "segunda hipótesis privada");
        var snapshot = await _store.BuildJointRefinementSnapshotAsync(batch.Id, revision.Id);
        var judgment = Judgment(snapshot);

        var saved = await _store.SaveJointRefinementEvaluationAsync(batch.Id, revision.Id, snapshot,
            Evaluator, judgment, DateTimeOffset.UtcNow);
        var restored = Assert.Single(await _store.ListTranscriptRefinementEvaluationsAsync(batch.Id));
        var raw = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(DatabasePath));

        Assert.Equal(TranscriptRefinementEvaluationPolicy.Version2, restored.PolicyVersion);
        Assert.Equal(RefinementEvaluationRecommendation.HumanReview, saved.Recommendation);
        Assert.Null(saved.RecommendedProposalId);
        Assert.Equal(revision.Id, restored.Snapshot.QwenAsr!.RevisionId);
        Assert.Equal(Hash, restored.Snapshot.QwenAsr.ModelHash);
        Assert.Equal(ModelRevisionProducer.Qwen3AsrLlamaCppV1, restored.Snapshot.QwenAsr.ProducerIdentity);
        Assert.Equal(Start, restored.Snapshot.Start);
        Assert.Equal(End, restored.Snapshot.End);
        Assert.Equal(judgment.ChoiceProbabilities, restored.Judgment!.ChoiceProbabilities);
        Assert.Equal("frase original privada", Assert.Single(await _store.GetSegmentsAsync(batch.SessionId)).Text);
        Assert.Empty(await _store.GetCorrectionsAsync(batch.SegmentId));
        Assert.DoesNotContain("segunda hipótesis privada", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("local-v1", raw, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("original")]
    [InlineData("proposal")]
    public async Task JointSnapshot_RejectsDuplicateCandidateText(string duplicate)
    {
        var batch = await CreateBatchAsync([new("propuesta privada corregida")]);
        var revision = await CreateRevisionAsync(batch,
            duplicate == "original" ? "frase original privada" : "propuesta privada corregida");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _store.BuildJointRefinementSnapshotAsync(batch.Id, revision.Id));
        Assert.Empty(await _store.ListTranscriptRefinementEvaluationsAsync(batch.Id));
    }

    [Fact]
    public async Task JointEvaluation_ConcurrentCorrectionRejectsStaleSnapshot()
    {
        var batch = await CreateBatchAsync([]);
        var revision = await CreateRevisionAsync(batch, "segunda hipótesis privada");
        var snapshot = await _store.BuildJointRefinementSnapshotAsync(batch.Id, revision.Id);
        await _store.SaveCorrectionAsync(batch.SegmentId, "corrección humana", "editor");

        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.SaveJointRefinementEvaluationAsync(
            batch.Id, revision.Id, snapshot, Evaluator, Judgment(snapshot), DateTimeOffset.UtcNow));
        Assert.Empty(await _store.ListTranscriptRefinementEvaluationsAsync(batch.Id));
    }

    [Theory]
    [InlineData("missing-hash")]
    [InlineData("failed")]
    [InlineData("bad-hash")]
    [InlineData("wrong-source")]
    [InlineData("wrong-session")]
    [InlineData("outside-interval")]
    [InlineData("other-producer")]
    [InlineData("legacy-null")]
    [InlineData("future")]
    [InlineData("wrong-scope")]
    public async Task JointSnapshot_RejectsUnverifiedOrWrongRevision(string fault)
    {
        var batch = await CreateBatchAsync([]);
        var scope = fault == "wrong-scope"
            ? new TranscriptModelRevisionScope("other", Start, End)
            : new TranscriptModelRevisionScope(batch.SegmentId, Start, End);
        if (fault == "wrong-scope")
            await _store.SaveSegmentAsync(new TranscriptSegment("other", batch.SessionId,
                batch.Source, 2, Start, End, "otro original", DateTimeOffset.UtcNow));
        var revision = fault == "other-producer"
            ? await _store.StartSegmentModelRevisionAsync(batch.SessionId, batch.Source,
                scope, "qwen3-asr", null, "es")
            : await _store.StartQwenSegmentModelRevisionAsync(batch.SessionId, batch.Source,
                scope, "qwen3-asr", null, "es");
        if (fault != "missing-hash")
            await _store.SetModelRevisionVerifiedModelHashAsync(revision.Id, fault == "bad-hash" ? "FAKE-VERIFIED-HASH" : Hash);
        await _store.SaveModelRevisionSegmentAsync(new TranscriptModelRevisionSegment(
            Guid.NewGuid().ToString("N"), revision.Id, 0, Start,
            fault == "outside-interval" ? End.Add(TimeSpan.FromMilliseconds(1)) : End,
            "otra hipótesis privada"));
        await _store.FinishModelRevisionAsync(revision.Id,
            fault == "failed" ? ModelRevisionStatus.Failed : ModelRevisionStatus.Succeeded,
            DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1), fault == "failed" ? "falló" : null);
        if (fault is "wrong-source" or "wrong-session" or "legacy-null" or "other-producer")
        {
            if (fault == "wrong-session")
                await _store.CreateSessionAsync(new MeetingSession("other-session", "Other",
                    DateTimeOffset.UtcNow.AddMinutes(-10), DateTimeOffset.UtcNow, SessionState.Completed));
            await using var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = fault switch
            {
                "wrong-source" => "UPDATE transcript_model_revisions SET source=$value WHERE id=$id",
                "wrong-session" => "UPDATE transcript_model_revisions SET session_id=$value WHERE id=$id",
                "other-producer" => "UPDATE transcript_model_revisions SET producer_identity=$value WHERE id=$id",
                _ => "UPDATE transcript_model_revisions SET producer_identity=$value WHERE id=$id"
            };
            command.Parameters.AddWithValue("$value", fault switch
            {
                "wrong-source" => (object)(int)AudioSourceKind.Microphone,
                "wrong-session" => "other-session",
                "other-producer" => "whisper.net/v1",
                _ => DBNull.Value
            });
            command.Parameters.AddWithValue("$id", revision.Id);
            await command.ExecuteNonQueryAsync();
        }
        if (fault == "future")
        {
            await using var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE transcript_model_revisions SET started_at=$future,ended_at=$future WHERE id=$id";
            command.Parameters.AddWithValue("$future", DateTimeOffset.UtcNow.AddHours(1).ToString("O"));
            command.Parameters.AddWithValue("$id", revision.Id);
            await command.ExecuteNonQueryAsync();
        }

        if (fault == "bad-hash")
            await Assert.ThrowsAsync<ArgumentException>(() =>
                _store.BuildJointRefinementSnapshotAsync(batch.Id, revision.Id));
        else
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _store.BuildJointRefinementSnapshotAsync(batch.Id, revision.Id));
    }

    private static RefinementEvaluationJudgment Judgment(RefinementEvaluationSnapshot snapshot)
    {
        var qwen = snapshot.QwenAsr!.ChoiceId;
        var probabilities = new Dictionary<string, double>
        {
            [qwen] = 0.90,
            [TranscriptRefinementEvaluationPolicy.KeepOriginalChoice] = 0.06,
            [TranscriptRefinementEvaluationPolicy.HumanReviewChoice] = 0.04
        };
        var nouls = new List<RefinementCandidateNouls> { new(qwen, 0.99, 0.01) };
        foreach (var proposal in snapshot.Proposals)
        {
            probabilities[qwen] -= 0.10;
            probabilities[proposal.ProposalId] = 0.10;
            nouls.Add(new(proposal.ProposalId, 0.99, 0.01));
        }
        return new(qwen, 0.99, probabilities, nouls);
    }

    private async Task<TranscriptRefinementBatch> CreateBatchAsync(IReadOnlyList<TranscriptRefinementDraft> drafts)
    {
        var session = new MeetingSession("session", "Meeting", DateTimeOffset.UtcNow.AddMinutes(-10),
            DateTimeOffset.UtcNow, SessionState.Completed);
        await _store.CreateSessionAsync(session);
        await _store.SaveSegmentAsync(new TranscriptSegment("segment", session.Id,
            AudioSourceKind.SystemOutput, 1, Start, End, "frase original privada", DateTimeOffset.UtcNow));
        return await _store.CreateRefinementBatchAsync("segment", "generator", new string('a', 64),
            drafts, DateTimeOffset.UtcNow);
    }

    private async Task<TranscriptModelRevision> CreateRevisionAsync(TranscriptRefinementBatch batch, string text)
    {
        var revision = await _store.StartQwenSegmentModelRevisionAsync(batch.SessionId, batch.Source,
            new TranscriptModelRevisionScope(batch.SegmentId, Start, End), "qwen3-asr", null, "es");
        await _store.SetModelRevisionVerifiedModelHashAsync(revision.Id, Hash);
        await _store.SaveModelRevisionSegmentAsync(new TranscriptModelRevisionSegment(
            Guid.NewGuid().ToString("N"), revision.Id, 0, Start, End, text));
        await _store.FinishModelRevisionAsync(revision.Id, ModelRevisionStatus.Succeeded,
            DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1), null);
        return revision;
    }
}
