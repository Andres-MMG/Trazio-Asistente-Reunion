using System.Text.Json;
using System.Text.Json.Serialization;

namespace Trazio.AsistenteReunion.TranscriptEvaluation;

public sealed record EvaluationManifest(
    int SchemaVersion,
    string MeaningErrorKindsVersion,
    ConsentRecord Consent,
    IReadOnlyList<EvaluationClip> Clips,
    IReadOnlyList<EvaluationConfiguration> Configurations,
    IReadOnlyList<EvaluationHypothesis> Hypotheses);

public sealed record ConsentRecord(
    string ConsentId,
    string Scope,
    DateTimeOffset GrantedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string EvidenceRelativePath,
    string EvidenceSha256);

public sealed record EvaluationClip(
    string ClipId,
    string AudioRelativePath,
    string AudioSha256,
    string ConsentReference,
    string ApprovedReferenceText,
    string ReferenceApprovedBy,
    DateTimeOffset ReferenceApprovedAtUtc,
    IReadOnlyList<string> Tags);

public sealed record EvaluationConfiguration(
    string ConfigurationId,
    string TranscriptionKind,
    string ModelId,
    string ModelSha256,
    string SettingsSha256,
    string HardwareId);

public sealed record EvaluationHypothesis(
    string ClipId,
    string ConfigurationId,
    string Text,
    double LatencyMs,
    long PeakWorkingSetBytes,
    HumanMeaningReview MeaningReview);

public sealed record HumanMeaningReview(
    bool ChangedMeaning,
    IReadOnlyList<string> ErrorKinds,
    string ReviewedBy,
    DateTimeOffset ReviewedAtUtc);

public sealed record ConfigurationReport(
    string ConfigurationId,
    string TranscriptionKind,
    string ModelId,
    string ModelSha256,
    string SettingsSha256,
    string HardwareId,
    int ClipCount,
    double Wer,
    double Cer,
    int MeaningErrorCount,
    IReadOnlyDictionary<string, int> MeaningErrorKinds,
    double LatencyP50Ms,
    double LatencyP95Ms,
    long PeakWorkingSetP50Bytes,
    long PeakWorkingSetP95Bytes);

public sealed record EvaluationReport(
    int SchemaVersion,
    string MeaningErrorKindsVersion,
    string ConsentId,
    int ClipCount,
    string NormalizationVersion,
    IReadOnlyList<ConfigurationReport> Configurations);

public sealed class EvaluationException(string message) : Exception(message);

public static class EvaluationJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.Strict,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
}

public static class MeaningErrorVocabulary
{
    public const string Version = "stage9-meaning-errors-v1";
    private static readonly HashSet<string> Allowed =
        ["negation", "number", "date", "name", "omission", "unsupported_fact", "other"];

    public static bool IsValid(string? kind) => kind is not null && Allowed.Contains(kind);
}
