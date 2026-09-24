using System.Text.Encodings.Web;
using System.Text.Json;

namespace Trazio.AsistenteReunion.VisualEvaluation;

public static class VisualEvaluationReportVersions
{
    public const int CurrentSchema = 1;
}

public sealed record VisualEvaluationReport(
    int SchemaVersion,
    string CorpusId,
    string Scope,
    IReadOnlyList<VisualEvaluationSuiteReport> Suites);

public sealed record VisualEvaluationSuiteReport(
    string SuiteId,
    string Provider,
    int ProfileVersion,
    int EvidenceVersion,
    int DetectorVersion,
    int ExpectedFeatureCount,
    IReadOnlyList<VisualEvaluationCandidateReport> Candidates);

public sealed record VisualEvaluationCandidateReport(
    string CandidateId,
    int PolicyVersion,
    VisualEvaluationCounts Metrics,
    VisualEvaluationDenominators Denominators,
    VisualEvaluationRatios Ratios,
    VisualEvaluationLatency LatencyMs,
    VisualEvaluationResources Resources,
    IReadOnlyList<VisualEvaluationScenarioReport> Scenarios);

public sealed record VisualEvaluationScenarioReport(
    string ScenarioId,
    VisualEvaluationCounts Metrics,
    VisualEvaluationDenominators Denominators,
    VisualEvaluationRatios Ratios,
    VisualEvaluationLatency LatencyMs,
    VisualEvaluationResources Resources);

public sealed record VisualEvaluationCounts(
    long Match,
    long Miss,
    long FalseMatch,
    long Abstain,
    long Unavailable,
    long Insufficient,
    long MicrophoneExcluded)
{
    public static VisualEvaluationCounts operator +(
        VisualEvaluationCounts left,
        VisualEvaluationCounts right) => new(
            left.Match + right.Match,
            left.Miss + right.Miss,
            left.FalseMatch + right.FalseMatch,
            left.Abstain + right.Abstain,
            left.Unavailable + right.Unavailable,
            left.Insufficient + right.Insufficient,
            left.MicrophoneExcluded + right.MicrophoneExcluded);
}

public sealed record VisualEvaluationDenominators(
    long Total,
    long Evaluated,
    long RemoteActivity,
    long RemoteInactive,
    long RemoteUnavailable)
{
    public static VisualEvaluationDenominators operator +(
        VisualEvaluationDenominators left,
        VisualEvaluationDenominators right) => new(
            left.Total + right.Total,
            left.Evaluated + right.Evaluated,
            left.RemoteActivity + right.RemoteActivity,
            left.RemoteInactive + right.RemoteInactive,
            left.RemoteUnavailable + right.RemoteUnavailable);
}

public sealed record VisualEvaluationRatios(
    int? MatchPpm,
    int? MissPpm,
    int? FalseMatchPpm,
    int? AbstainPpm,
    int? UnavailablePpm,
    int? InsufficientPpm,
    int? MicrophoneExcludedPpm)
{
    internal static VisualEvaluationRatios Create(
        VisualEvaluationCounts counts,
        VisualEvaluationDenominators denominators) => new(
            Ratio(counts.Match, denominators.RemoteActivity),
            Ratio(counts.Miss, denominators.RemoteActivity),
            Ratio(counts.FalseMatch, denominators.RemoteInactive + denominators.RemoteUnavailable),
            Ratio(counts.Abstain, denominators.Evaluated),
            Ratio(counts.Unavailable, denominators.Evaluated),
            Ratio(counts.Insufficient, denominators.Evaluated),
            Ratio(counts.MicrophoneExcluded, denominators.Total));

    private static int? Ratio(long numerator, long denominator) => denominator == 0
        ? null
        : checked((int)(numerator * 1_000_000L / denominator));
}

public sealed record VisualEvaluationLatency(
    int SampleCount,
    long? P50Ms,
    long? P95Ms,
    long? MaxMs)
{
    public static VisualEvaluationLatency Create(IReadOnlyList<long> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0) return new(0, null, null, null);
        return new(
            samples.Count,
            VisualEvaluationMath.NearestRank(samples, 50),
            VisualEvaluationMath.NearestRank(samples, 95),
            samples.Max());
    }
}

public sealed record VisualEvaluationResources(
    long DetectorCalls,
    long Observations,
    long FeatureCells,
    long CorrelationCalls,
    long EvidenceIntervals,
    long PeakRetainedIntervals,
    int MaxFeatures)
{
    internal static VisualEvaluationResources Aggregate(IEnumerable<VisualEvaluationResources> resources)
    {
        var values = resources.ToArray();
        return values.Length == 0
            ? new(0, 0, 0, 0, 0, 0, 0)
            : new(
                values.Sum(value => value.DetectorCalls),
                values.Sum(value => value.Observations),
                values.Sum(value => value.FeatureCells),
                values.Sum(value => value.CorrelationCalls),
                values.Sum(value => value.EvidenceIntervals),
                values.Max(value => value.PeakRetainedIntervals),
                values.Max(value => value.MaxFeatures));
    }
}

public static class VisualEvaluationMath
{
    public static long NearestRank(IReadOnlyList<long> samples, int percentile)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0) throw new ArgumentException("At least one sample is required.", nameof(samples));
        if (percentile is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(percentile));

        var ordered = samples.Order().ToArray();
        var rank = checked((percentile * ordered.Length + 99) / 100);
        return ordered[rank - 1];
    }
}

public static class VisualEvaluationReportSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Default
    };

    public static string Serialize(VisualEvaluationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return JsonSerializer.Serialize(report, Options).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
    }
}
