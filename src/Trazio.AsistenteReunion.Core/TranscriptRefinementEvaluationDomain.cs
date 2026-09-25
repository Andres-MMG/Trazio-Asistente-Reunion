using System.Text;

namespace Trazio.AsistenteReunion.Core;

public enum RefinementEvaluationRecommendation { KeepOriginal, SuggestProposal, HumanReview }

public sealed record RefinementEvaluationCandidate(string ProposalId, string Text);

public sealed record RefinementObservedAsrCandidate(
    string RevisionId, string SegmentId, string ChoiceId, string Text,
    string ModelIdentity, string ModelHash, string Language,
    AudioSourceKind Source, TimeSpan Start, TimeSpan End,
    string? ProducerIdentity = null);

public sealed record RefinementEvaluationSnapshot(
    string BatchId,
    string SessionId,
    AudioSourceKind Source,
    TimeSpan Start,
    TimeSpan End,
    string OriginalText,
    IReadOnlyList<RefinementEvaluationCandidate> Proposals,
    RefinementObservedAsrCandidate? QwenAsr = null,
    int? CorrectionRevision = null);

public sealed record RefinementCandidateNouls(
    string ProposalId,
    double SemanticPreservation,
    double UnsupportedFacts);

public sealed record RefinementEvaluationJudgment(
    string ChoiceId,
    double ChoiceConfidence,
    IReadOnlyDictionary<string, double> ChoiceProbabilities,
    IReadOnlyList<RefinementCandidateNouls> CandidateNouls);

public enum RefinementJevFallbackReason { NotInstalled, Capacity, Unavailable }

public sealed record RefinementEvaluatorIdentity(
    string Model, string ModelVersion, string QuestionVersion,
    string? BundleFingerprint = null,
    RefinementJevFallbackReason? JevFallbackReason = null);

public sealed record TranscriptRefinementEvaluation(
    string Id,
    RefinementEvaluationSnapshot Snapshot,
    RefinementEvaluatorIdentity? Evaluator,
    string PolicyVersion,
    RefinementEvaluationJudgment? Judgment,
    RefinementEvaluationRecommendation Recommendation,
    string? RecommendedProposalId,
    DateTimeOffset CreatedAt);

public static class TranscriptRefinementEvaluationPolicy
{
    public const string Version1 = "local-refinement-v1";
    public const string Version2 = "local-refinement-joint-v2";
    public const string Version = Version1;
    public const string KeepOriginalChoice = "keep_original";
    public const string HumanReviewChoice = "human_review";
    private const double Version1MinimumChoiceConfidence = 0.75;
    private const double Version1MinimumWinnerProbability = 0.75;
    private const double Version1MinimumWinnerMargin = 0.20;
    private const double Version1MinimumSemanticPreservation = 0.85;
    private const double Version1MaximumUnsupportedFacts = 0.10;

    public static (RefinementEvaluationRecommendation Recommendation, string? ProposalId) Decide(
        RefinementEvaluationSnapshot snapshot,
        RefinementEvaluationJudgment? judgment) => DecideForVersion(Version, snapshot, judgment);

    public static (RefinementEvaluationRecommendation Recommendation, string? ProposalId) DecideForVersion(
        string policyVersion,
        RefinementEvaluationSnapshot snapshot,
        RefinementEvaluationJudgment? judgment) => policyVersion switch
    {
        Version1 => DecideVersion1(snapshot, judgment),
        Version2 => DecideVersion2(snapshot, judgment),
        _ => throw new NotSupportedException(
            "La versión histórica de esta evaluación no es compatible con la aplicación actual. Actualiza la aplicación para revisarla.")
    };

    public static string QwenChoiceId(string revisionId) => "qwen_asr_" + revisionId;

    private static (RefinementEvaluationRecommendation Recommendation, string? ProposalId) DecideVersion2(
        RefinementEvaluationSnapshot snapshot, RefinementEvaluationJudgment? judgment)
    {
        ValidateJointSnapshot(snapshot);
        var candidateIds = new HashSet<string>(snapshot.Proposals.Select(item => item.ProposalId), StringComparer.Ordinal)
        {
            snapshot.QwenAsr!.ChoiceId
        };
        ArgumentNullException.ThrowIfNull(judgment);
        var choices = new HashSet<string>(candidateIds, StringComparer.Ordinal) { KeepOriginalChoice, HumanReviewChoice };
        if (choices.Count != candidateIds.Count + 2 ||
            !choices.Contains(judgment.ChoiceId) ||
            judgment.ChoiceProbabilities is null || judgment.ChoiceProbabilities.Count != choices.Count ||
            judgment.CandidateNouls is null || judgment.CandidateNouls.Count != candidateIds.Count)
            throw new ArgumentException("El juicio no cubre todos los candidatos.", nameof(judgment));
        ValidateProbability(judgment.ChoiceConfidence);
        double sum = 0;
        foreach (var (choice, probability) in judgment.ChoiceProbabilities)
        {
            if (!choices.Contains(choice)) throw new ArgumentException("El juicio contiene una opción ajena.", nameof(judgment));
            ValidateProbability(probability);
            sum += probability;
        }
        if (Math.Abs(sum - 1.0) > 0.001)
            throw new ArgumentException("La distribución no suma uno.", nameof(judgment));
        var judgedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in judgment.CandidateNouls)
        {
            if (candidate is null || !candidateIds.Contains(candidate.ProposalId) ||
                !judgedIds.Add(candidate.ProposalId))
                throw new ArgumentException("Falta un juicio semántico o está duplicado.", nameof(judgment));
            ValidateProbability(candidate.SemanticPreservation);
            ValidateProbability(candidate.UnsupportedFacts);
        }
        // Neither Whisper nor Qwen is an acoustic ground truth. Joint pilot decisions remain human-owned.
        return (RefinementEvaluationRecommendation.HumanReview, null);
    }

    public static void ValidateJointSnapshot(RefinementEvaluationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var qwen = snapshot.QwenAsr ?? throw new ArgumentException("Falta la hipótesis acústica de Qwen.", nameof(snapshot));
        if (string.IsNullOrWhiteSpace(snapshot.BatchId) || string.IsNullOrWhiteSpace(snapshot.SessionId) ||
            !Enum.IsDefined(snapshot.Source) || snapshot.Start < TimeSpan.Zero || snapshot.End <= snapshot.Start ||
            snapshot.Proposals is null || snapshot.Proposals.Count > 2 ||
            snapshot.CorrectionRevision is null or < 0 ||
            string.IsNullOrWhiteSpace(qwen.RevisionId) || qwen.RevisionId.Length > 64 ||
            string.IsNullOrWhiteSpace(qwen.SegmentId) || qwen.SegmentId.Length > 64 ||
            qwen.ChoiceId != QwenChoiceId(qwen.RevisionId) ||
            qwen.Source != snapshot.Source || qwen.Start != snapshot.Start || qwen.End != snapshot.End ||
            qwen.ProducerIdentity != ModelRevisionProducer.Qwen3AsrLlamaCppV1 ||
            !IsBounded(qwen.ModelIdentity) || !IsBounded(qwen.Language) || !IsVerifiedQwenHash(qwen.ModelHash))
            throw new ArgumentException("La hipótesis acústica no coincide con el segmento original.", nameof(snapshot));
        TranscriptRefinementPolicy.ValidateText(snapshot.OriginalText, nameof(snapshot));
        TranscriptRefinementPolicy.ValidateText(qwen.Text, nameof(snapshot));
        var seenText = new HashSet<string>(StringComparer.Ordinal) { NormalizeCandidateText(snapshot.OriginalText) };
        if (!seenText.Add(NormalizeCandidateText(qwen.Text)))
            throw new ArgumentException("La segunda transcripción duplica el original.", nameof(snapshot));
        var candidateIds = new HashSet<string>(StringComparer.Ordinal) { qwen.ChoiceId };
        foreach (var proposal in snapshot.Proposals)
        {
            if (proposal is null || string.IsNullOrWhiteSpace(proposal.ProposalId) ||
                proposal.ProposalId.Length > 64 || !candidateIds.Add(proposal.ProposalId))
                throw new ArgumentException("Las propuestas no tienen identidades únicas.", nameof(snapshot));
            TranscriptRefinementPolicy.ValidateText(proposal.Text, nameof(snapshot));
            if (!seenText.Add(NormalizeCandidateText(proposal.Text)))
                throw new ArgumentException("Dos candidatos tienen el mismo texto.", nameof(snapshot));
        }
    }

    private static bool IsVerifiedQwenHash(string? hash)
    {
        if (hash is null) return false;
        var parts = hash.Split(';');
        return parts.Length == 2 && parts[0].StartsWith("SHA256:", StringComparison.Ordinal) &&
            parts[0].Length == 71 && parts[0].AsSpan(7).IndexOfAnyExcept("0123456789ABCDEF") < 0 &&
            parts[1].StartsWith("MMPROJ-SHA256:", StringComparison.Ordinal) &&
            parts[1].Length == 78 && parts[1].AsSpan(14).IndexOfAnyExcept("0123456789ABCDEF") < 0;
    }

    private static string NormalizeCandidateText(string text) =>
        string.Join(' ', text.Trim().Normalize(NormalizationForm.FormC)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();

    private static (RefinementEvaluationRecommendation Recommendation, string? ProposalId) DecideVersion1(
        RefinementEvaluationSnapshot snapshot,
        RefinementEvaluationJudgment? judgment)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.QwenAsr is not null)
            throw new ArgumentException("La evaluación histórica v1 no admite un candidato acústico.", nameof(snapshot));
        if (string.IsNullOrWhiteSpace(snapshot.BatchId) || string.IsNullOrWhiteSpace(snapshot.SessionId) ||
            !Enum.IsDefined(snapshot.Source) || snapshot.Start < TimeSpan.Zero || snapshot.End <= snapshot.Start ||
            snapshot.Proposals is null || snapshot.Proposals.Count > 2)
            throw new ArgumentException("El lote de refinamiento no es válido.", nameof(snapshot));
        TranscriptRefinementPolicy.ValidateText(snapshot.OriginalText, nameof(snapshot));
        var proposalIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var proposal in snapshot.Proposals)
        {
            if (proposal is null || string.IsNullOrWhiteSpace(proposal.ProposalId) ||
                !proposalIds.Add(proposal.ProposalId))
                throw new ArgumentException("Las propuestas del lote no son válidas.", nameof(snapshot));
            TranscriptRefinementPolicy.ValidateText(proposal.Text, nameof(snapshot));
        }
        if (proposalIds.Count == 0)
        {
            if (judgment is not null)
                throw new ArgumentException("Un lote sin propuestas no admite juicios de modelo.", nameof(judgment));
            return (RefinementEvaluationRecommendation.KeepOriginal, null);
        }
        ArgumentNullException.ThrowIfNull(judgment);
        var expectedChoices = new HashSet<string>(proposalIds, StringComparer.Ordinal)
        {
            KeepOriginalChoice, HumanReviewChoice
        };
        if (expectedChoices.Count != proposalIds.Count + 2 ||
            string.IsNullOrWhiteSpace(judgment.ChoiceId) ||
            !expectedChoices.Contains(judgment.ChoiceId) ||
            judgment.ChoiceProbabilities is null || judgment.ChoiceProbabilities.Count != expectedChoices.Count ||
            judgment.CandidateNouls is null || judgment.CandidateNouls.Count != proposalIds.Count)
            throw new ArgumentException("El juicio no cubre exactamente las opciones del lote.", nameof(judgment));
        ValidateProbability(judgment.ChoiceConfidence);
        double total = 0;
        foreach (var (id, probability) in judgment.ChoiceProbabilities)
        {
            if (!expectedChoices.Contains(id))
                throw new ArgumentException("El juicio contiene una opción ajena al lote.", nameof(judgment));
            ValidateProbability(probability);
            total += probability;
        }
        if (Math.Abs(total - 1.0) > 0.001)
            throw new ArgumentException("La distribución de opciones no suma uno.", nameof(judgment));
        var noulIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var nouls in judgment.CandidateNouls)
        {
            if (nouls is null || !proposalIds.Contains(nouls.ProposalId) || !noulIds.Add(nouls.ProposalId))
                throw new ArgumentException("Falta una evaluación semántica o hay una duplicada.", nameof(judgment));
            ValidateProbability(nouls.SemanticPreservation);
            ValidateProbability(nouls.UnsupportedFacts);
        }
        var winner = judgment.ChoiceProbabilities[judgment.ChoiceId];
        var runnerUp = judgment.ChoiceProbabilities
            .Where(item => item.Key != judgment.ChoiceId)
            .Max(item => item.Value);
        if (judgment.ChoiceConfidence < Version1MinimumChoiceConfidence ||
            winner < Version1MinimumWinnerProbability || winner - runnerUp < Version1MinimumWinnerMargin ||
            judgment.ChoiceId == HumanReviewChoice)
            return (RefinementEvaluationRecommendation.HumanReview, null);
        if (judgment.ChoiceId == KeepOriginalChoice)
            return (RefinementEvaluationRecommendation.KeepOriginal, null);
        var candidate = judgment.CandidateNouls.Single(item => item.ProposalId == judgment.ChoiceId);
        if (candidate.SemanticPreservation < Version1MinimumSemanticPreservation ||
            candidate.UnsupportedFacts > Version1MaximumUnsupportedFacts)
            return (RefinementEvaluationRecommendation.HumanReview, null);
        return (RefinementEvaluationRecommendation.SuggestProposal, judgment.ChoiceId);
    }

    public static void ValidateEvaluator(RefinementEvaluatorIdentity? evaluator, bool hasProposals)
    {
        if (!hasProposals)
        {
            if (evaluator is not null)
                throw new ArgumentException("Un lote sin propuestas no debe invocar un evaluador.", nameof(evaluator));
            return;
        }
        if (evaluator is null ||
            !IsBounded(evaluator.Model) || !IsBounded(evaluator.ModelVersion) ||
            !IsBounded(evaluator.QuestionVersion) ||
            (evaluator.BundleFingerprint is not null && !IsSha256Fingerprint(evaluator.BundleFingerprint)) ||
            (evaluator.Model == "jev" ?
                evaluator.JevFallbackReason is null || !Enum.IsDefined(evaluator.JevFallbackReason.Value) ||
                evaluator.BundleFingerprint is not null : evaluator.JevFallbackReason is not null))
            throw new ArgumentException("La identidad y versión del evaluador no son válidas.", nameof(evaluator));
    }

    private static bool IsSha256Fingerprint(string value) =>
        value.StartsWith("sha256:", StringComparison.Ordinal) && value.Length == 71 &&
        value.AsSpan(7).IndexOfAnyExcept("0123456789ABCDEF") < 0;

    private static bool IsBounded(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 200 && !value.Any(char.IsControl);

    private static void ValidateProbability(double value)
    {
        if (!double.IsFinite(value) || value is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(value), "Las probabilidades deben ser finitas y estar entre cero y uno.");
    }
}
