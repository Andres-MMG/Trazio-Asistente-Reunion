using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.Tests;

public sealed class TranscriptRefinementEvaluationTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "trazio-refinement-evaluation-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private AesContentProtector _protector = null!;
    private SqliteSessionStore _store = null!;
    private string DatabasePath => Path.Combine(_directory, "test.db");
    private const string Fingerprint = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private static readonly RefinementEvaluatorIdentity Evaluator = new("laya-local", "1.0", "meeting-questions-v1");

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
    public async Task SaveEvaluation_HighPreferenceWithSemanticRisk_RequiresHumanReview()
    {
        var batch = await CreateBatchAsync();
        var proposalId = batch.Proposals[0].Id;
        var judgment = Judgment(proposalId, new(proposalId, 0.60, 0.01));

        var saved = await _store.SaveTranscriptRefinementEvaluationAsync(batch.Id, Evaluator, judgment, DateTimeOffset.UtcNow);

        Assert.Equal(RefinementEvaluationRecommendation.HumanReview, saved.Recommendation);
        Assert.Null(saved.RecommendedProposalId);
        Assert.Equal("frase original privada", Assert.Single(await _store.GetSegmentsAsync(batch.SessionId)).Text);
        Assert.Empty(await _store.GetCorrectionsAsync(batch.SegmentId));
    }

    [Fact]
    public async Task SaveEvaluation_UnsupportedFactsCannotBeCompensatedByPreference()
    {
        var batch = await CreateBatchAsync();
        var proposalId = batch.Proposals[0].Id;
        var judgment = Judgment(proposalId, new(proposalId, 0.99, 0.20));

        var saved = await _store.SaveTranscriptRefinementEvaluationAsync(batch.Id, Evaluator, judgment, DateTimeOffset.UtcNow);

        Assert.Equal(RefinementEvaluationRecommendation.HumanReview, saved.Recommendation);
    }

    [Fact]
    public async Task SaveEvaluation_SafeHighConfidenceOnlySuggestsAndRoundTripsEncrypted()
    {
        var batch = await CreateBatchAsync();
        var proposalId = batch.Proposals[0].Id;
        var judgment = Judgment(proposalId, new(proposalId, 0.99, 0.01));

        var saved = await _store.SaveTranscriptRefinementEvaluationAsync(batch.Id, Evaluator, judgment, DateTimeOffset.UtcNow);
        var restored = Assert.Single(await _store.ListTranscriptRefinementEvaluationsAsync(batch.Id));
        var raw = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(DatabasePath));

        Assert.Equal(RefinementEvaluationRecommendation.SuggestProposal, restored.Recommendation);
        Assert.Equal(proposalId, restored.RecommendedProposalId);
        Assert.Equal(saved.Id, restored.Id);
        Assert.Equal(batch.Source, restored.Snapshot.Source);
        Assert.Equal(batch.Start, restored.Snapshot.Start);
        Assert.Equal(batch.End, restored.Snapshot.End);
        Assert.Equal(batch.OriginalText, restored.Snapshot.OriginalText);
        Assert.Equal(batch.Proposals[0].Text, Assert.Single(restored.Snapshot.Proposals).Text);
        Assert.Equal(Evaluator, restored.Evaluator);
        Assert.Equal(judgment.ChoiceId, restored.Judgment!.ChoiceId);
        Assert.Equal(judgment.CandidateNouls[0], Assert.Single(restored.Judgment.CandidateNouls));
        Assert.Equal(TranscriptRefinementEvaluationPolicy.Version, restored.PolicyVersion);
        Assert.DoesNotContain("frase original privada", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("propuesta privada corregida", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("laya-local", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("meeting-questions-v1", raw, StringComparison.Ordinal);
        Assert.Equal("frase original privada", Assert.Single(await _store.GetSegmentsAsync(batch.SessionId)).Text);
        Assert.Empty(await _store.GetCorrectionsAsync(batch.SegmentId));
    }

    [Fact]
    public async Task SaveEvaluation_ActualLayaBundleFingerprintRoundTripsEncrypted()
    {
        var batch = await CreateBatchAsync();
        var proposalId = batch.Proposals[0].Id;
        var fingerprint = "sha256:" + new string('A', 64);
        var evaluator = Evaluator with { BundleFingerprint = fingerprint };

        await _store.SaveTranscriptRefinementEvaluationAsync(batch.Id, evaluator,
            Judgment(proposalId, new(proposalId, 0.99, 0.01)), DateTimeOffset.UtcNow);
        var restored = Assert.Single(await _store.ListTranscriptRefinementEvaluationsAsync(batch.Id));
        var raw = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(DatabasePath));

        Assert.Equal(fingerprint, restored.Evaluator!.BundleFingerprint);
        Assert.DoesNotContain(fingerprint, raw, StringComparison.Ordinal);
        Assert.Equal("frase original privada", Assert.Single(await _store.GetSegmentsAsync(batch.SessionId)).Text);
        Assert.Throws<ArgumentException>(() => TranscriptRefinementEvaluationPolicy.ValidateEvaluator(
            evaluator with { BundleFingerprint = "sha256:wrong" }, true));
    }

    [Fact]
    public async Task SaveEvaluation_JevFallbackReasonRoundTripsEncryptedAndNeverEditsOriginal()
    {
        var batch = await CreateBatchAsync();
        var proposalId = batch.Proposals[0].Id;
        var evaluator = new RefinementEvaluatorIdentity(
            "jev", "jev-1.13.0", "refinement-jev-questions-v1",
            JevFallbackReason: RefinementJevFallbackReason.Unavailable);

        await _store.SaveTranscriptRefinementEvaluationAsync(batch.Id, evaluator,
            Judgment(proposalId, new(proposalId, 0.99, 0.01)), DateTimeOffset.UtcNow);
        var stored = Assert.Single(await _store.ListTranscriptRefinementEvaluationsAsync(batch.Id));
        var raw = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(DatabasePath));

        Assert.Equal(RefinementJevFallbackReason.Unavailable, stored.Evaluator?.JevFallbackReason);
        Assert.DoesNotContain("jev-1.13.0", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("Unavailable", raw, StringComparison.Ordinal);
        Assert.Equal("frase original privada", Assert.Single(await _store.GetSegmentsAsync(batch.SessionId)).Text);
        Assert.Empty(await _store.GetCorrectionsAsync(batch.SegmentId));
        Assert.Throws<ArgumentException>(() => TranscriptRefinementEvaluationPolicy.ValidateEvaluator(
            evaluator with { JevFallbackReason = null }, true));
    }
    [Fact]
    public async Task SaveEvaluation_NoProposalsRecordsDeterministicKeepOriginalWithoutModel()
    {
        var batch = await CreateBatchAsync([]);

        var saved = await _store.SaveTranscriptRefinementEvaluationAsync(batch.Id, null, null, DateTimeOffset.UtcNow);
        var restored = Assert.Single(await _store.ListTranscriptRefinementEvaluationsAsync(batch.Id));

        Assert.Equal(RefinementEvaluationRecommendation.KeepOriginal, saved.Recommendation);
        Assert.Null(restored.Judgment);
        Assert.Null(restored.Evaluator);
        Assert.Throws<ArgumentException>(() => TranscriptRefinementEvaluationPolicy.ValidateEvaluator(Evaluator, false));
    }

    [Fact]
    public async Task SaveEvaluation_InvalidDistributionOrNoulValues_FailsWithoutRows()
    {
        var batch = await CreateBatchAsync();
        var id = batch.Proposals[0].Id;
        var valid = Judgment(id, new(id, 0.99, 0.01));
        var malformed = new RefinementEvaluationJudgment[]
        {
            valid with { ChoiceProbabilities = new Dictionary<string, double> { [id] = 1.0 } },
            valid with { ChoiceProbabilities = new Dictionary<string, double>
                { [id] = 0.8, [TranscriptRefinementEvaluationPolicy.KeepOriginalChoice] = 0.1,
                  [TranscriptRefinementEvaluationPolicy.HumanReviewChoice] = 0.0 } },
            valid with { ChoiceProbabilities = new Dictionary<string, double>
                { [id] = double.NaN, [TranscriptRefinementEvaluationPolicy.KeepOriginalChoice] = 0.1,
                  [TranscriptRefinementEvaluationPolicy.HumanReviewChoice] = 0.0 } },
            valid with { CandidateNouls = [new(id, double.PositiveInfinity, 0.01)] },
            valid with { CandidateNouls = [new("unknown", 0.99, 0.01)] },
            valid with { ChoiceId = "unknown" }
        };
        foreach (var judgment in malformed)
            await Assert.ThrowsAnyAsync<ArgumentException>(() =>
                _store.SaveTranscriptRefinementEvaluationAsync(batch.Id, Evaluator, judgment, DateTimeOffset.UtcNow));
        Assert.Empty(await _store.ListTranscriptRefinementEvaluationsAsync(batch.Id));
    }

    [Fact]
    public async Task DeleteSession_CascadesEvaluationAndDoesNotRequireArbitrationRevision()
    {
        var batch = await CreateBatchAsync();
        var id = batch.Proposals[0].Id;
        await _store.SaveTranscriptRefinementEvaluationAsync(batch.Id, Evaluator,
            Judgment(id, new(id, 0.99, 0.01)), DateTimeOffset.UtcNow);

        await _store.DeleteSessionAsync(batch.SessionId);

        Assert.Empty(await _store.ListTranscriptRefinementEvaluationsAsync(batch.Id));
        await using var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM transcript_refinement_evaluations";
        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task SaveEvaluation_TwoProposals_RequiresIndependentNoulsForBoth()
    {
        var batch = await CreateBatchAsync([new("primera propuesta"), new("segunda alternativa", true)]);
        var first = batch.Proposals[0].Id;
        var second = batch.Proposals[1].Id;
        var incomplete = new RefinementEvaluationJudgment(first, 0.98,
            new Dictionary<string, double>
            {
                [first] = 0.82, [second] = 0.10,
                [TranscriptRefinementEvaluationPolicy.KeepOriginalChoice] = 0.04,
                [TranscriptRefinementEvaluationPolicy.HumanReviewChoice] = 0.04
            }, [new(first, 0.99, 0.01)]);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _store.SaveTranscriptRefinementEvaluationAsync(batch.Id, Evaluator, incomplete, DateTimeOffset.UtcNow));
        var complete = incomplete with { CandidateNouls = [new(first, 0.99, 0.01), new(second, 0.70, 0.40)] };
        var saved = await _store.SaveTranscriptRefinementEvaluationAsync(batch.Id, Evaluator, complete, DateTimeOffset.UtcNow);

        Assert.Equal(RefinementEvaluationRecommendation.SuggestProposal, saved.Recommendation);
        Assert.Equal(first, saved.RecommendedProposalId);
        Assert.Equal(2, Assert.Single(await _store.ListTranscriptRefinementEvaluationsAsync(batch.Id)).Judgment!.CandidateNouls.Count);
    }

    [Fact]
    public async Task SaveEvaluation_UncertaintyRequiresReviewEvenWhenSelectedProposalIsSemanticallySafe()
    {
        var batch = await CreateBatchAsync();
        var id = batch.Proposals[0].Id;
        var judgment = Judgment(id, new(id, 0.99, 0.01)) with { ChoiceConfidence = 0.50 };

        var saved = await _store.SaveTranscriptRefinementEvaluationAsync(batch.Id, Evaluator, judgment, DateTimeOffset.UtcNow);

        Assert.Equal(RefinementEvaluationRecommendation.HumanReview, saved.Recommendation);
        Assert.Null(saved.RecommendedProposalId);
    }

    [Fact]
    public async Task SaveEvaluation_AppendOnlyKeepsEveryIndependentResult()
    {
        var batch = await CreateBatchAsync();
        var id = batch.Proposals[0].Id;
        var first = await _store.SaveTranscriptRefinementEvaluationAsync(batch.Id, Evaluator,
            Judgment(id, new(id, 0.60, 0.01)), DateTimeOffset.UtcNow.AddSeconds(-1));
        var second = await _store.SaveTranscriptRefinementEvaluationAsync(batch.Id, Evaluator,
            Judgment(id, new(id, 0.99, 0.01)), DateTimeOffset.UtcNow);

        var records = await _store.ListTranscriptRefinementEvaluationsAsync(batch.Id);
        Assert.Equal([first.Id, second.Id], records.Select(item => item.Id));
        Assert.Equal(RefinementEvaluationRecommendation.HumanReview, records[0].Recommendation);
        Assert.Equal(RefinementEvaluationRecommendation.SuggestProposal, records[1].Recommendation);
    }
    [Fact]
    public async Task ReadVersion1Evaluation_UsesFrozenVersion1RulesEvenWithFuturePolicyIdentifier()
    {
        var batch = await CreateBatchAsync();
        var proposalId = batch.Proposals[0].Id;
        var judgment = new RefinementEvaluationJudgment(
            proposalId, 0.76,
            new Dictionary<string, double>
            {
                [proposalId] = 0.76,
                [TranscriptRefinementEvaluationPolicy.KeepOriginalChoice] = 0.14,
                [TranscriptRefinementEvaluationPolicy.HumanReviewChoice] = 0.10
            },
            [new(proposalId, 0.86, 0.09)]);
        var saved = await _store.SaveTranscriptRefinementEvaluationAsync(
            batch.Id, Evaluator, judgment, DateTimeOffset.UtcNow);

        Assert.Equal(TranscriptRefinementEvaluationPolicy.Version1, saved.PolicyVersion);
        Assert.Equal(RefinementEvaluationRecommendation.SuggestProposal, saved.Recommendation);
        Assert.Throws<NotSupportedException>(() =>
            TranscriptRefinementEvaluationPolicy.DecideForVersion(
                "local-refinement-v2", saved.Snapshot, judgment));

        var restored = Assert.Single(await _store.ListTranscriptRefinementEvaluationsAsync(batch.Id));
        Assert.Equal(TranscriptRefinementEvaluationPolicy.Version1, restored.PolicyVersion);
        Assert.Equal(saved.Recommendation, restored.Recommendation);
        Assert.Equal(saved.RecommendedProposalId, restored.RecommendedProposalId);
        Assert.Equal((RefinementEvaluationRecommendation.SuggestProposal, proposalId),
            TranscriptRefinementEvaluationPolicy.DecideForVersion(
                restored.PolicyVersion, restored.Snapshot, restored.Judgment));
    }

    private static RefinementEvaluationJudgment Judgment(string proposalId, RefinementCandidateNouls nouls) =>
        new(proposalId, 0.98,
            new Dictionary<string, double>
            {
                [proposalId] = 0.90,
                [TranscriptRefinementEvaluationPolicy.KeepOriginalChoice] = 0.06,
                [TranscriptRefinementEvaluationPolicy.HumanReviewChoice] = 0.04
            },
            [nouls]);

    private async Task<TranscriptRefinementBatch> CreateBatchAsync(IReadOnlyList<TranscriptRefinementDraft>? drafts = null)
    {
        var session = new MeetingSession("session", "Meeting", DateTimeOffset.UtcNow.AddMinutes(-10),
            DateTimeOffset.UtcNow, SessionState.Completed);
        await _store.CreateSessionAsync(session);
        var segment = new TranscriptSegment("segment", session.Id, AudioSourceKind.SystemOutput, 1,
            TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), "frase original privada", DateTimeOffset.UtcNow);
        await _store.SaveSegmentAsync(segment);
        return await _store.CreateRefinementBatchAsync(segment.Id, "generator", Fingerprint,
            drafts ?? [new("propuesta privada corregida")], DateTimeOffset.UtcNow);
    }
}
