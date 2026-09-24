using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Trazio.AsistenteReunion.App;
using Trazio.AsistenteReunion.Core;
using Trazio.AsistenteReunion.VisualEvaluation;

namespace Trazio.AsistenteReunion.Tests;

public sealed class VisualEvaluationRunnerTests
{
    [Fact]
    [Trait("Area", "VisualCapture")]
    public void Evaluate_UsesExplicitMetricDenominatorsAndExcludesMicrophone()
    {
        var report = Evaluate(VisualEvaluationCorpusTests.ValidCorpusJson());
        var scenario = report.Suites[0].Candidates[0].Scenarios[0];

        Assert.Equal(new VisualEvaluationCounts(1, 0, 0, 0, 0, 1, 1), scenario.Metrics);
        Assert.Equal(new VisualEvaluationDenominators(3, 2, 1, 1, 0), scenario.Denominators);
        Assert.Equal(1_000_000, scenario.Ratios.MatchPpm);
        Assert.Equal(500_000, scenario.Ratios.InsufficientPpm);
        Assert.Equal(333_333, scenario.Ratios.MicrophoneExcludedPpm);
        Assert.Equal(4, scenario.Resources.DetectorCalls);
        Assert.Equal(3, scenario.Resources.Observations);
        Assert.Equal(18, scenario.Resources.FeatureCells);
        Assert.Equal(5, scenario.Resources.CorrelationCalls);
        Assert.Equal(2, scenario.Resources.EvidenceIntervals);
        Assert.Equal(2, scenario.Resources.PeakRetainedIntervals);
        Assert.Equal(2, scenario.Resources.MaxFeatures);
        Assert.Equal(new VisualEvaluationLatency(1, 3_000, 3_000, 3_000), scenario.LatencyMs);
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void Evaluate_DifferentCandidatesAreIsolated()
    {
        var secondCandidate = """
            ,{
              "id":"candidate-002",
              "policyVersion":1,
              "detector":{"enterScore":800,"exitScore":200,"activationHoldMs":2000,"releaseHoldMs":0,"maxObservationGapMs":2000,"minimumCoherentFeatures":2},
              "correlation":{"minimumConfidencePpm":750000,"minimumCoveragePpm":800000,"minimumActivityOverlapPpm":250000}
            }
            """;
        var json = VisualEvaluationCorpusTests.ValidCorpusJson()
            .Replace("}],\n    \"scenarios\"", "}" + secondCandidate + "],\n    \"scenarios\"", StringComparison.Ordinal);

        var report = Evaluate(json);
        var candidates = report.Suites[0].Candidates;

        Assert.Equal(2, candidates.Count);
        Assert.Equal(1, candidates[0].Metrics.Match);
        Assert.Equal(0, candidates[0].Metrics.Miss);
        Assert.Equal(0, candidates[1].Metrics.Match);
        Assert.Equal(1, candidates[1].Metrics.Miss);
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void NearestRank_UsesDeterministicP50P95AndMaximum()
    {
        var summary = VisualEvaluationLatency.Create([9, 1, 4, 2, 7]);

        Assert.Equal(5, summary.SampleCount);
        Assert.Equal(4, summary.P50Ms);
        Assert.Equal(9, summary.P95Ms);
        Assert.Equal(9, summary.MaxMs);
        Assert.Equal(9, VisualEvaluationMath.NearestRank([9, 1, 4, 2, 7], 95));
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void Evaluate_FirstEligibleCheckpointWithoutNewEvidence_RecordsActualLatency()
    {
        var root = JsonNode.Parse(VisualEvaluationCorpusTests.ValidCorpusJson())!;
        var candidate = root["suites"]![0]!["candidates"]![0]!;
        candidate["correlation"]!["minimumCoveragePpm"] = 500_000;
        candidate["correlation"]!["minimumActivityOverlapPpm"] = 500_000;
        var scenario = root["suites"]![0]!["scenarios"]![0]!;
        scenario["observations"] = JsonNode.Parse("""
            [
              {"atMs":0,"surfaceRevision":1,"status":"available","features":[{"highlightMatchPpm":900000,"nonBlackPpm":900000,"meanLumaPpm":900000},{"highlightMatchPpm":900000,"nonBlackPpm":900000,"meanLumaPpm":900000}]},
              {"atMs":500,"surfaceRevision":1,"status":"unavailable","features":[]},
              {"atMs":1000,"surfaceRevision":1,"status":"unavailable","features":[]}
            ]
            """);
        scenario["completionAtMs"] = 2_000;
        scenario["segments"] = JsonNode.Parse(
            "[{\"source\":\"systemOutput\",\"startMs\":0,\"endMs\":1000,\"truth\":\"remoteActivity\"}]");

        var report = Evaluate(root.ToJsonString()).Suites[0].Candidates[0].Scenarios[0];

        Assert.Equal(new VisualEvaluationLatency(1, 1_000, 1_000, 1_000), report.LatencyMs);
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void Evaluate_CorrelationWorkAtBudgetLimit_IsAccepted()
    {
        var json = WithRepeatedRemoteActivitySegments(
            VisualEvaluationCorpusTests.ValidCorpusJson(),
            VisualEvaluationLimits.MaximumSegmentsPerScenario);

        var scenario = Evaluate(json).Suites[0].Candidates[0].Scenarios[0];

        Assert.Equal(VisualEvaluationLimits.MaximumSegmentsPerScenario, scenario.Metrics.Match);
        Assert.Equal(8_192L, VisualEvaluationLimits.MaximumCorrelationWorkPerScenario);
        Assert.Equal(VisualEvaluationLimits.MaximumSegmentsPerScenario * 3L, scenario.Resources.CorrelationCalls);
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void Evaluate_ValidLookingQuadraticCorrelationWorkOverBudget_IsRejectedEarly()
    {
        var json = WithRepeatedRemoteActivitySegments(
                VisualEvaluationCorpusTests.ValidCorpusJson(),
                VisualEvaluationLimits.MaximumSegmentsPerScenario)
            .Replace("\"atMs\":2000,\"surfaceRevision\":1", "\"atMs\":2000,\"surfaceRevision\":2", StringComparison.Ordinal);

        var error = Assert.Throws<VisualEvaluationCorpusException>(() => Evaluate(json));

        Assert.Equal("The corpus exceeds the deterministic evaluation work budget.", error.Message);
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void Evaluate_DetectorFeatureWorkAtBudgetLimit_IsAccepted()
    {
        var corpus = WithDetectorWork(
            checked((int)(VisualEvaluationLimits.MaximumDetectorFeatureWorkPerScenario /
                          VisualEvaluationLimits.MaximumFeaturesPerObservation)));

        var scenario = new VisualEvaluationRunner().Evaluate(corpus).Suites[0].Candidates[0].Scenarios[0];

        Assert.Equal(VisualEvaluationLimits.MaximumDetectorFeatureWorkPerScenario / 16 + 1, scenario.Resources.DetectorCalls);
        Assert.Equal(VisualEvaluationLimits.MaximumDetectorFeatureWorkPerScenario * 3, scenario.Resources.FeatureCells);
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void Evaluate_DetectorFeatureWorkOverBudget_IsRejectedBeforeDetectorCall()
    {
        var atLimit = checked((int)(VisualEvaluationLimits.MaximumDetectorFeatureWorkPerScenario /
                                    VisualEvaluationLimits.MaximumFeaturesPerObservation));
        var corpus = WithDetectorWork(atLimit + 1);

        var error = Assert.Throws<VisualEvaluationCorpusException>(() => new VisualEvaluationRunner().Evaluate(corpus));

        Assert.Equal("The corpus exceeds the deterministic evaluation work budget.", error.Message);
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void Evaluate_MicrophoneSegmentIsExcludedEvenWithIdenticalEvidence()
    {
        var scenario = Evaluate(VisualEvaluationCorpusTests.ValidCorpusJson())
            .Suites[0].Candidates[0].Scenarios[0];

        Assert.Equal(1, scenario.Metrics.MicrophoneExcluded);
        Assert.Equal(2, scenario.Denominators.Evaluated);
        Assert.Equal(0, scenario.Metrics.FalseMatch);
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void Evaluate_ExplicitUnavailableCoverageIsReportedSeparately()
    {
        var json = VisualEvaluationCorpusTests.ValidCorpusJson()
            .Replace(
                "\"status\":\"available\",\"features\":[{\"highlightMatchPpm\":100000,\"nonBlackPpm\":900000,\"meanLumaPpm\":100000},{\"highlightMatchPpm\":100000,\"nonBlackPpm\":900000,\"meanLumaPpm\":100000}]",
                "\"status\":\"unavailable\",\"features\":[]",
                StringComparison.Ordinal)
            .Replace("\"truth\":\"remoteInactive\"", "\"truth\":\"remoteUnavailable\"", StringComparison.Ordinal);

        var scenario = Evaluate(json).Suites[0].Candidates[0].Scenarios[0];

        Assert.Equal(1, scenario.Metrics.Match);
        Assert.Equal(1, scenario.Metrics.Unavailable);
        Assert.Equal(1, scenario.Metrics.MicrophoneExcluded);
        Assert.Equal(0, scenario.Metrics.Insufficient);
        Assert.Equal(new VisualEvaluationLatency(1, 2_000, 2_000, 2_000), scenario.LatencyMs);
        Assert.Equal(4, scenario.Resources.CorrelationCalls);
        Assert.Equal(3, scenario.Resources.EvidenceIntervals);
        Assert.Equal(3, scenario.Resources.PeakRetainedIntervals);
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void Evaluate_SurfaceDiscontinuity_CoversAbstainAndFalseMatch()
    {
        const string lowFeatures = "\"features\":[{\"highlightMatchPpm\":100000,\"nonBlackPpm\":900000,\"meanLumaPpm\":100000},{\"highlightMatchPpm\":100000,\"nonBlackPpm\":900000,\"meanLumaPpm\":100000}]";
        const string highFeatures = "\"features\":[{\"highlightMatchPpm\":900000,\"nonBlackPpm\":900000,\"meanLumaPpm\":900000},{\"highlightMatchPpm\":900000,\"nonBlackPpm\":900000,\"meanLumaPpm\":900000}]";
        var json = VisualEvaluationCorpusTests.ValidCorpusJson()
            .Replace("\"atMs\":2000,\"surfaceRevision\":1", "\"atMs\":2000,\"surfaceRevision\":2", StringComparison.Ordinal)
            .Replace(lowFeatures, highFeatures, StringComparison.Ordinal);

        var scenario = Evaluate(json).Suites[0].Candidates[0].Scenarios[0];

        Assert.Equal(1, scenario.Metrics.Abstain);
        Assert.Equal(1, scenario.Metrics.FalseMatch);
        Assert.Equal(1, scenario.Metrics.MicrophoneExcluded);
        Assert.Equal(0, scenario.LatencyMs.SampleCount);
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void Evaluate_MicrophoneOnlyRatios_AreNullWhenDenominatorIsUndefined()
    {
        var json = WithSegments(
            VisualEvaluationCorpusTests.ValidCorpusJson(),
            "[{\"source\":\"microphone\",\"startMs\":0,\"endMs\":1000,\"truth\":\"microphoneExcluded\"}]");

        var report = Evaluate(json);
        var ratios = report.Suites[0].Candidates[0].Scenarios[0].Ratios;

        Assert.Null(ratios.MatchPpm);
        Assert.Null(ratios.MissPpm);
        Assert.Null(ratios.FalseMatchPpm);
        Assert.Null(ratios.AbstainPpm);
        Assert.Null(ratios.UnavailablePpm);
        Assert.Null(ratios.InsufficientPpm);
        Assert.Equal(1_000_000, ratios.MicrophoneExcludedPpm);
        using var serialized = JsonDocument.Parse(VisualEvaluationReportSerializer.Serialize(report));
        var jsonRatios = serialized.RootElement.GetProperty("suites")[0]
            .GetProperty("candidates")[0]
            .GetProperty("scenarios")[0]
            .GetProperty("ratios");
        Assert.Equal(JsonValueKind.Null, jsonRatios.GetProperty("matchPpm").ValueKind);
        Assert.Equal(JsonValueKind.Null, jsonRatios.GetProperty("abstainPpm").ValueKind);
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void Evaluate_MissingNegativeTruthClass_HasNullFalseMatchRatio()
    {
        var json = WithSegments(
            VisualEvaluationCorpusTests.ValidCorpusJson(),
            "[{\"source\":\"systemOutput\",\"startMs\":0,\"endMs\":2000,\"truth\":\"remoteActivity\"}]");

        var ratios = Evaluate(json).Suites[0].Candidates[0].Scenarios[0].Ratios;

        Assert.Equal(1_000_000, ratios.MatchPpm);
        Assert.Equal(0, ratios.MissPpm);
        Assert.Null(ratios.FalseMatchPpm);
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void Serialize_RepeatedRunsAndCulturesAreByteIdentical()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("es-CL");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("es-CL");
            var first = VisualEvaluationReportSerializer.Serialize(Evaluate(VisualEvaluationCorpusTests.ValidCorpusJson()));

            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr-TR");
            var second = VisualEvaluationReportSerializer.Serialize(Evaluate(VisualEvaluationCorpusTests.ValidCorpusJson()));

            Assert.Equal(first, second);
            Assert.DoesNotContain('\r', first);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void Serialize_ReportUsesExactSchemaAndNeverEchoesFeatureTrace()
    {
        var json = VisualEvaluationReportSerializer.Serialize(Evaluate(VisualEvaluationCorpusTests.ValidCorpusJson()));
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(VisualEvaluationReportVersions.CurrentSchema, root.GetProperty("schemaVersion").GetInt32());
        AssertProperties(root, "schemaVersion", "corpusId", "scope", "suites");
        var suite = root.GetProperty("suites")[0];
        AssertProperties(suite, "suiteId", "provider", "profileVersion", "evidenceVersion", "detectorVersion", "expectedFeatureCount", "candidates");
        var candidate = suite.GetProperty("candidates")[0];
        AssertProperties(candidate, "candidateId", "policyVersion", "metrics", "denominators", "ratios", "latencyMs", "resources", "scenarios");
        AssertProperties(candidate.GetProperty("metrics"), "match", "miss", "falseMatch", "abstain", "unavailable", "insufficient", "microphoneExcluded");
        AssertProperties(candidate.GetProperty("denominators"), "total", "evaluated", "remoteActivity", "remoteInactive", "remoteUnavailable");
        AssertProperties(candidate.GetProperty("ratios"), "matchPpm", "missPpm", "falseMatchPpm", "abstainPpm", "unavailablePpm", "insufficientPpm", "microphoneExcludedPpm");
        AssertProperties(candidate.GetProperty("latencyMs"), "sampleCount", "p50Ms", "p95Ms", "maxMs");
        AssertProperties(candidate.GetProperty("resources"), "detectorCalls", "observations", "featureCells", "correlationCalls", "evidenceIntervals", "peakRetainedIntervals", "maxFeatures");
        var scenario = candidate.GetProperty("scenarios")[0];
        AssertProperties(scenario, "scenarioId", "metrics", "denominators", "ratios", "latencyMs", "resources");
        AssertProperties(scenario.GetProperty("metrics"), "match", "miss", "falseMatch", "abstain", "unavailable", "insufficient", "microphoneExcluded");
        AssertProperties(scenario.GetProperty("denominators"), "total", "evaluated", "remoteActivity", "remoteInactive", "remoteUnavailable");
        AssertProperties(scenario.GetProperty("ratios"), "matchPpm", "missPpm", "falseMatchPpm", "abstainPpm", "unavailablePpm", "insufficientPpm", "microphoneExcludedPpm");
        AssertProperties(scenario.GetProperty("latencyMs"), "sampleCount", "p50Ms", "p95Ms", "maxMs");
        AssertProperties(scenario.GetProperty("resources"), "detectorCalls", "observations", "featureCells", "correlationCalls", "evidenceIntervals", "peakRetainedIntervals", "maxFeatures");
        Assert.DoesNotContain("912345", json, StringComparison.Ordinal);
        Assert.DoesNotContain("123456", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"features\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("segments", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("highlight", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("luma", json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [Trait("Area", "VisualCapture")]
    [InlineData(MeetingProvider.GoogleMeet)]
    [InlineData(MeetingProvider.MicrosoftTeams)]
    public void ProductionProfiles_RemainUnvalidatedAndEmpty(MeetingProvider provider)
    {
        var profile = VisualProbeProfiles.GetProduction(provider);

        Assert.Equal(VisualProbeProfileValidationState.Unvalidated, profile.ValidationState);
        Assert.Equal(0, profile.ExpectedFeatureCount);
        Assert.Null(profile.DetectionPolicy);
        Assert.Equal("No disponible", AnonymousVisualEvidenceViewModel.Unavailable.StatusText);
    }

    private static VisualEvaluationReport Evaluate(string json) =>
        new VisualEvaluationRunner().Evaluate(VisualEvaluationCorpusLoader.LoadJson(json));

    private static string WithSegments(string json, string segmentsJson)
    {
        var root = JsonNode.Parse(json)!;
        root["suites"]![0]!["scenarios"]![0]!["segments"] = JsonNode.Parse(segmentsJson);
        return root.ToJsonString();
    }

    private static string WithRepeatedRemoteActivitySegments(string json, int count)
    {
        var segments = new JsonArray();
        for (var index = 0; index < count; index++)
        {
            segments.Add(new JsonObject
            {
                ["source"] = "systemOutput",
                ["startMs"] = 0,
                ["endMs"] = 2_000,
                ["truth"] = "remoteActivity"
            });
        }

        var root = JsonNode.Parse(json)!;
        root["suites"]![0]!["scenarios"]![0]!["segments"] = segments;
        return root.ToJsonString();
    }

    private static VisualEvaluationCorpus WithDetectorWork(int observationCount)
    {
        var corpus = VisualEvaluationCorpusLoader.LoadJson(VisualEvaluationCorpusTests.ValidCorpusJson());
        var suite = corpus.Suites[0];
        var scenario = suite.Scenarios[0];
        var features = Enumerable.Range(0, VisualEvaluationLimits.MaximumFeaturesPerObservation)
            .Select(_ => new VisualEvaluationFeature
            {
                HighlightMatchPpm = 900_000,
                NonBlackPpm = 900_000,
                MeanLumaPpm = 900_000
            })
            .ToArray();
        var observations = Enumerable.Range(0, observationCount)
            .Select(index => new VisualEvaluationObservation
            {
                AtMs = index,
                SurfaceRevision = 1,
                Status = VisualEvaluationObservationStatus.Available,
                Features = features
            })
            .ToArray();
        var updatedScenario = scenario with
        {
            Observations = observations,
            CompletionAtMs = observationCount,
            Segments =
            [
                new VisualEvaluationSegment
                {
                    Source = VisualEvaluationAudioSource.SystemOutput,
                    StartMs = 0,
                    EndMs = 1,
                    Truth = VisualEvaluationTruth.RemoteActivity
                }
            ]
        };
        var updatedSuite = suite with
        {
            ExpectedFeatureCount = VisualEvaluationLimits.MaximumFeaturesPerObservation,
            Scenarios = [updatedScenario]
        };
        return corpus with { Suites = [updatedSuite] };
    }

    private static void AssertProperties(JsonElement element, params string[] expected)
    {
        var actual = element.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal);
        Assert.Equal(expected.Order(StringComparer.Ordinal), actual);
    }
}
