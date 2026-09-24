using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.VisualEvaluation;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record VisualEvaluationCorpus
{
    public const int CurrentSchemaVersion = 1;
    public const string RequiredScope = "synthetic-aggregate-only";

    public required int SchemaVersion { get; init; }
    public required string CorpusId { get; init; }
    public required string Scope { get; init; }
    public required IReadOnlyList<VisualEvaluationSuite> Suites { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record VisualEvaluationSuite
{
    public required string Id { get; init; }
    public required string Provider { get; init; }
    public required int ProfileVersion { get; init; }
    public required int EvidenceVersion { get; init; }
    public required int DetectorVersion { get; init; }
    public required int ExpectedFeatureCount { get; init; }
    public required IReadOnlyList<VisualEvaluationCandidate> Candidates { get; init; }
    public required IReadOnlyList<VisualEvaluationScenario> Scenarios { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record VisualEvaluationCandidate
{
    public required string Id { get; init; }
    public required int PolicyVersion { get; init; }
    public required VisualEvaluationDetectorParameters Detector { get; init; }
    public required VisualEvaluationCorrelationThresholds Correlation { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record VisualEvaluationDetectorParameters
{
    public required int EnterScore { get; init; }
    public required int ExitScore { get; init; }
    public required long ActivationHoldMs { get; init; }
    public required long ReleaseHoldMs { get; init; }
    public required long MaxObservationGapMs { get; init; }
    public required int MinimumCoherentFeatures { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record VisualEvaluationCorrelationThresholds
{
    public required int MinimumConfidencePpm { get; init; }
    public required int MinimumCoveragePpm { get; init; }
    public required int MinimumActivityOverlapPpm { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record VisualEvaluationScenario
{
    public required string Id { get; init; }
    public required IReadOnlyList<VisualEvaluationObservation> Observations { get; init; }
    public required long CompletionAtMs { get; init; }
    public required IReadOnlyList<VisualEvaluationSegment> Segments { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record VisualEvaluationObservation
{
    public required long AtMs { get; init; }
    public required long SurfaceRevision { get; init; }
    public required VisualEvaluationObservationStatus Status { get; init; }
    public required IReadOnlyList<VisualEvaluationFeature> Features { get; init; }
}

public enum VisualEvaluationObservationStatus
{
    Available = 0,
    Unavailable = 1
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record VisualEvaluationFeature
{
    public required int HighlightMatchPpm { get; init; }
    public required int NonBlackPpm { get; init; }
    public required int MeanLumaPpm { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record VisualEvaluationSegment
{
    public required VisualEvaluationAudioSource Source { get; init; }
    public required long StartMs { get; init; }
    public required long EndMs { get; init; }
    public required VisualEvaluationTruth Truth { get; init; }
}

public enum VisualEvaluationAudioSource
{
    SystemOutput = 0,
    Microphone = 1
}

public enum VisualEvaluationTruth
{
    RemoteActivity = 0,
    RemoteInactive = 1,
    RemoteUnavailable = 2,
    MicrophoneExcluded = 3
}

public sealed class VisualEvaluationCorpusException(string message) : Exception(message);

public static class VisualEvaluationLimits
{
    public const int MaximumCorpusBytes = 2 * 1024 * 1024;
    public const int MaximumJsonDepth = 24;
    public const int MaximumSuites = 8;
    public const int MaximumCandidatesPerSuite = 16;
    public const int MaximumScenariosPerSuite = 128;
    public const int MaximumObservationsPerScenario = 4_096;
    public const int MaximumSegmentsPerScenario = 512;
    public const int MaximumFeaturesPerObservation = 16;
    public const int MaximumGeneratedIntervalsPerScenario = 8_192;
    public const long MaximumDetectorFeatureWorkPerScenario = 16_384;
    public const long MaximumCorrelationWorkPerScenario = 8_192;
}

public static class VisualEvaluationCorpusLoader
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    public static VisualEvaluationCorpus Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan);
        if (stream.Length > VisualEvaluationLimits.MaximumCorpusBytes)
            throw new VisualEvaluationCorpusException("The corpus exceeds the byte limit.");

        var utf8 = GC.AllocateUninitializedArray<byte>(checked((int)stream.Length));
        stream.ReadExactly(utf8);
        return LoadUtf8(utf8);
    }

    public static VisualEvaluationCorpus LoadJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (Encoding.UTF8.GetByteCount(json) > VisualEvaluationLimits.MaximumCorpusBytes)
            throw new VisualEvaluationCorpusException("The corpus exceeds the byte limit.");
        if (string.IsNullOrWhiteSpace(json)) throw new JsonException("The visual-evaluation corpus cannot be empty.");
        var corpus = JsonSerializer.Deserialize<VisualEvaluationCorpus>(json, Options)
            ?? throw new JsonException("The visual-evaluation corpus cannot be null.");
        VisualEvaluationCorpusValidator.Validate(corpus);
        return corpus;
    }

    private static VisualEvaluationCorpus LoadUtf8(ReadOnlySpan<byte> utf8)
    {
        if (utf8.IsEmpty) throw new JsonException("The visual-evaluation corpus cannot be empty.");
        var corpus = JsonSerializer.Deserialize<VisualEvaluationCorpus>(utf8, Options)
            ?? throw new JsonException("The visual-evaluation corpus cannot be null.");
        VisualEvaluationCorpusValidator.Validate(corpus);
        return corpus;
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            AllowDuplicateProperties = false,
            MaxDepth = VisualEvaluationLimits.MaximumJsonDepth,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        options.Converters.Add(new CanonicalEnumConverter<VisualEvaluationObservationStatus>());
        options.Converters.Add(new CanonicalEnumConverter<VisualEvaluationAudioSource>());
        options.Converters.Add(new CanonicalEnumConverter<VisualEvaluationTruth>());
        return options;
    }

    private sealed class CanonicalEnumConverter<TEnum> : JsonConverter<TEnum>
        where TEnum : struct, Enum
    {
        private static readonly IReadOnlyDictionary<string, TEnum> Values = Enum.GetValues<TEnum>()
            .ToDictionary(ToToken, value => value, StringComparer.Ordinal);

        public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String ||
                reader.GetString() is not { } token ||
                !Values.TryGetValue(token, out var value))
                throw new JsonException("The enum token is not canonical.");
            return value;
        }

        public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options) =>
            writer.WriteStringValue(ToToken(value));

        private static string ToToken(TEnum value)
        {
            var name = value.ToString();
            return string.Create(name.Length, name, static (characters, source) =>
            {
                source.AsSpan().CopyTo(characters);
                characters[0] = char.ToLowerInvariant(characters[0]);
            });
        }
    }
}

internal static class VisualEvaluationCorpusValidator
{
    private const int PartsPerMillion = 1_000_000;
    private const long MaximumTimeSpanMilliseconds = long.MaxValue / TimeSpan.TicksPerMillisecond;

    public static void Validate(VisualEvaluationCorpus corpus)
    {
        if (corpus.SchemaVersion != VisualEvaluationCorpus.CurrentSchemaVersion)
            Reject("schemaVersion must be 1.");
        ValidateToken(corpus.CorpusId, "corpus");
        if (!string.Equals(corpus.Scope, VisualEvaluationCorpus.RequiredScope, StringComparison.Ordinal))
            Reject($"scope must be '{VisualEvaluationCorpus.RequiredScope}'.");
        RequireNonEmpty(corpus.Suites, "suites");
        RequireMaximum(corpus.Suites, VisualEvaluationLimits.MaximumSuites, "suites");
        RequireNoNulls(corpus.Suites, "suites");
        RequireUnique(corpus.Suites, suite => suite.Id, "suite IDs");

        foreach (var suite in corpus.Suites) ValidateSuite(suite);
    }

    private static void ValidateSuite(VisualEvaluationSuite suite)
    {
        ValidateToken(suite.Id, "suite");
        if (suite.Provider is not ("google-meet" or "microsoft-teams"))
            Reject("suite.provider must be a normalized supported provider.");
        if (suite.ProfileVersion != AnonymousVisualEvidenceVersions.CurrentProfile ||
            suite.EvidenceVersion != AnonymousVisualEvidenceVersions.CurrentEvidence ||
            suite.DetectorVersion != AnonymousVisualEvidenceVersions.CurrentDetector)
            Reject("suite provenance versions must match the current implementation.");
        if (suite.ExpectedFeatureCount is <= 0 or > VisualEvaluationLimits.MaximumFeaturesPerObservation)
            Reject("suite.expectedFeatureCount exceeds the supported feature bound.");
        RequireNonEmpty(suite.Candidates, "suite.candidates");
        RequireNonEmpty(suite.Scenarios, "suite.scenarios");
        RequireMaximum(suite.Candidates, VisualEvaluationLimits.MaximumCandidatesPerSuite, "suite.candidates");
        RequireMaximum(suite.Scenarios, VisualEvaluationLimits.MaximumScenariosPerSuite, "suite.scenarios");
        RequireNoNulls(suite.Candidates, "suite.candidates");
        RequireNoNulls(suite.Scenarios, "suite.scenarios");
        RequireUnique(suite.Candidates, candidate => candidate.Id, "candidate IDs within a suite");
        RequireUnique(suite.Scenarios, scenario => scenario.Id, "scenario IDs within a suite");

        foreach (var candidate in suite.Candidates) ValidateCandidate(candidate, suite.ExpectedFeatureCount);
        foreach (var scenario in suite.Scenarios) ValidateScenario(scenario, suite.ExpectedFeatureCount);
    }

    private static void ValidateCandidate(VisualEvaluationCandidate candidate, int expectedFeatureCount)
    {
        ValidateToken(candidate.Id, "candidate");
        if (candidate.PolicyVersion != AnonymousVisualEvidenceVersions.CurrentPolicy)
            Reject("candidate.policyVersion must match the current implementation.");
        if (candidate.Detector is null) Reject("candidate.detector is required.");
        if (candidate.Correlation is null) Reject("candidate.correlation is required.");

        var detector = candidate.Detector;
        ValidateScore(detector.EnterScore, "candidate.detector.enterScore");
        ValidateScore(detector.ExitScore, "candidate.detector.exitScore");
        if (detector.ExitScore >= detector.EnterScore)
            Reject("candidate.detector.exitScore must be lower than enterScore.");
        ValidateDuration(detector.ActivationHoldMs, allowZero: true, "candidate.detector.activationHoldMs");
        ValidateDuration(detector.ReleaseHoldMs, allowZero: true, "candidate.detector.releaseHoldMs");
        ValidateDuration(detector.MaxObservationGapMs, allowZero: false, "candidate.detector.maxObservationGapMs");
        if (detector.MinimumCoherentFeatures <= 0 || detector.MinimumCoherentFeatures > expectedFeatureCount)
            Reject("candidate.detector.minimumCoherentFeatures must fit expectedFeatureCount.");

        ValidatePpm(candidate.Correlation.MinimumConfidencePpm, "candidate.correlation.minimumConfidencePpm");
        ValidatePpm(candidate.Correlation.MinimumCoveragePpm, "candidate.correlation.minimumCoveragePpm");
        ValidatePpm(candidate.Correlation.MinimumActivityOverlapPpm, "candidate.correlation.minimumActivityOverlapPpm");
    }

    private static void ValidateScenario(VisualEvaluationScenario scenario, int expectedFeatureCount)
    {
        ValidateToken(scenario.Id, "scenario");
        RequireNonEmpty(scenario.Observations, "scenario.observations");
        RequireNonEmpty(scenario.Segments, "scenario.segments");
        RequireMaximum(
            scenario.Observations,
            VisualEvaluationLimits.MaximumObservationsPerScenario,
            "scenario.observations");
        RequireMaximum(
            scenario.Segments,
            VisualEvaluationLimits.MaximumSegmentsPerScenario,
            "scenario.segments");
        RequireNoNulls(scenario.Observations, "scenario.observations");
        RequireNoNulls(scenario.Segments, "scenario.segments");

        long? priorAtMs = null;
        foreach (var observation in scenario.Observations)
        {
            ValidateDuration(observation.AtMs, allowZero: true, "observation.atMs");
            RequireNonNegative(observation.SurfaceRevision, "observation.surfaceRevision");
            if (priorAtMs is not null && observation.AtMs <= priorAtMs.Value)
                Reject("scenario observations must have strictly increasing atMs values.");
            priorAtMs = observation.AtMs;
            if (!Enum.IsDefined(observation.Status)) Reject("observation.status is invalid.");
            if (observation.Features is null) Reject("observation.features is required.");
            RequireNoNulls(observation.Features, "observation.features");
            if (observation.Status == VisualEvaluationObservationStatus.Unavailable && observation.Features.Count != 0)
                Reject("unavailable observations cannot contain features.");
            if (observation.Status == VisualEvaluationObservationStatus.Available &&
                observation.Features.Count != expectedFeatureCount)
                Reject("available observations must contain expectedFeatureCount aggregate vectors.");
            foreach (var feature in observation.Features) ValidateFeature(feature);
        }

        ValidateDuration(scenario.CompletionAtMs, allowZero: true, "scenario.completionAtMs");
        if (scenario.CompletionAtMs < priorAtMs!.Value)
            Reject("scenario.completionAtMs cannot precede the last observation.");
        foreach (var segment in scenario.Segments) ValidateSegment(segment, scenario.CompletionAtMs);
    }

    private static void ValidateFeature(VisualEvaluationFeature feature)
    {
        ValidatePpm(feature.HighlightMatchPpm, "feature.highlightMatchPpm");
        ValidatePpm(feature.NonBlackPpm, "feature.nonBlackPpm");
        ValidatePpm(feature.MeanLumaPpm, "feature.meanLumaPpm");
    }

    private static void ValidateSegment(VisualEvaluationSegment segment, long completionAtMs)
    {
        if (!Enum.IsDefined(segment.Source)) Reject("segment.source is invalid.");
        if (!Enum.IsDefined(segment.Truth)) Reject("segment.truth is invalid.");
        ValidateDuration(segment.StartMs, allowZero: true, "segment.startMs");
        ValidateDuration(segment.EndMs, allowZero: false, "segment.endMs");
        if (segment.EndMs <= segment.StartMs || segment.EndMs > completionAtMs)
            Reject("segment range must be positive and end by completionAtMs.");

        var microphone = segment.Source == VisualEvaluationAudioSource.Microphone;
        if (microphone != (segment.Truth == VisualEvaluationTruth.MicrophoneExcluded))
            Reject("microphone source and microphoneExcluded truth must be paired.");
    }

    private static void ValidateToken(string value, string prefix)
    {
        const int minimumDigits = 3;
        const int maximumDigits = 9;
        var expectedPrefix = prefix + "-";
        if (value is null ||
            !value.StartsWith(expectedPrefix, StringComparison.Ordinal) ||
            value.Length is < 7 or > 19)
            Reject("An identifier is not a valid opaque numeric token.");

        var digits = value.AsSpan(expectedPrefix.Length);
        if (digits.Length is < minimumDigits or > maximumDigits ||
            digits.ContainsAnyExceptInRange('0', '9') ||
            digits.IndexOfAnyExcept('0') < 0)
            Reject("An identifier is not a valid opaque numeric token.");
    }

    private static void ValidateScore(int value, string field)
    {
        if (value is < 0 or > 1_000) Reject($"{field} must be between 0 and 1000.");
    }

    private static void ValidatePpm(int value, string field)
    {
        if (value is < 0 or > PartsPerMillion) Reject($"{field} must be between 0 and 1000000.");
    }

    private static void RequireNonNegative(long value, string field)
    {
        if (value < 0) Reject($"{field} cannot be negative.");
    }

    private static void ValidateDuration(long value, bool allowZero, string field)
    {
        if (value < 0 || (!allowZero && value == 0) || value > MaximumTimeSpanMilliseconds)
            Reject($"{field} is outside the supported millisecond range.");
    }

    private static void RequireNonEmpty<T>(IReadOnlyList<T>? values, string field)
    {
        if (values is null || values.Count == 0) Reject($"{field} cannot be empty.");
    }

    private static void RequireMaximum<T>(IReadOnlyList<T> values, int maximum, string field)
    {
        if (values.Count > maximum) Reject($"{field} exceeds its item limit.");
    }

    private static void RequireUnique<T>(IReadOnlyList<T> values, Func<T, string> selector, string field)
    {
        if (values.Select(selector).Distinct(StringComparer.Ordinal).Count() != values.Count)
            Reject($"{field} must be unique.");
    }

    private static void RequireNoNulls<T>(IReadOnlyList<T> values, string field)
    {
        if (values.Any(value => value is null)) Reject($"{field} cannot contain null items.");
    }

    [DoesNotReturn]
    private static void Reject(string message) => throw new VisualEvaluationCorpusException(message);
}
