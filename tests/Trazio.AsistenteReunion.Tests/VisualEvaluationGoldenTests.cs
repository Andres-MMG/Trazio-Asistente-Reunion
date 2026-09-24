using System.Globalization;
using System.Text;
using System.Text.Json;
using Trazio.AsistenteReunion.App;
using Trazio.AsistenteReunion.Core;
using Trazio.AsistenteReunion.VisualEvaluation;

namespace Trazio.AsistenteReunion.Tests;

public sealed class VisualEvaluationGoldenTests
{
    [Fact]
    [Trait("Area", "VisualCapture")]
    public void CommittedGolden_IsBomlessUtf8LfAndProtectedByNarrowGitAttribute()
    {
        var bytes = File.ReadAllBytes(GoldenPath());

        Assert.False(bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }));
        Assert.DoesNotContain((byte)'\r', bytes);
        Assert.Equal((byte)'\n', bytes[^1]);
        _ = new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true)
            .GetString(bytes);
        var attributes = File.ReadAllLines(Path.Combine(RepositoryRoot(), ".gitattributes"));
        Assert.Contains("evaluation/stage-7b/*.golden.json text eol=lf", attributes);
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void CommittedGolden_RepeatedRunsAndCulturesAreByteIdentical()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            var expected = File.ReadAllBytes(GoldenPath());
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("es-CL");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("es-CL");
            var first = EvaluateCanonicalBytes();

            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr-TR");
            var second = EvaluateCanonicalBytes();

            Assert.Equal(expected, first);
            Assert.Equal(first, second);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void CliVerify_MatchingGolden_ReturnsStableSuccess()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = VisualEvaluationCli.Run(
            ["verify", "--corpus", CorpusPath(), "--golden", GoldenPath()],
            output,
            error);

        Assert.Equal(0, exitCode);
        Assert.Equal(VisualEvaluationCli.VerifiedMessage + Environment.NewLine, output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void CliVerify_MismatchedGolden_ReturnsStableSanitizedFailureWithoutRewriting()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "{}\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            var before = File.ReadAllBytes(path);
            var output = new StringWriter();
            var error = new StringWriter();

            var exitCode = VisualEvaluationCli.Run(
                ["verify", "--corpus", CorpusPath(), "--golden", path],
                output,
                error);

            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, output.ToString());
            Assert.Equal(VisualEvaluationCli.GoldenMismatchError + Environment.NewLine, error.ToString());
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.DoesNotContain(path, error.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("{}", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void CliVerify_TruncatedAndAppendedGolden_ReturnGoldenMismatch()
    {
        var canonical = EvaluateCanonicalBytes();
        AssertGoldenMismatch(canonical[..^1]);
        AssertGoldenMismatch([.. canonical, (byte)'x']);
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void CliVerify_SameLengthMutationBeyondFirstChunk_ReturnsGoldenMismatch()
    {
        var mutated = EvaluateCanonicalBytes();
        var mutationIndex = 8 * 1024 + 37;
        Assert.True(mutated.Length > mutationIndex);
        mutated[mutationIndex] ^= 0x01;

        AssertGoldenMismatch(mutated);
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void CliVerify_OversizedSparseGolden_ReturnsGoldenMismatchWithoutAllocation()
    {
        var path = Path.GetTempFileName();
        try
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
                stream.SetLength(int.MaxValue + 1L);

            AssertCliFailure(path, VisualEvaluationCli.GoldenMismatchError);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void CliVerify_MissingDirectoryAndInvalidGoldenPaths_ReturnSanitizedCorpusRejected()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"missing-golden-{Guid.NewGuid():N}.json");
        AssertCliFailure(missing, VisualEvaluationCli.CorpusRejectedError);
        AssertCliFailure(Path.GetTempPath(), VisualEvaluationCli.CorpusRejectedError);
        AssertCliFailure("\0", VisualEvaluationCli.CorpusRejectedError);
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void CliVerify_UnreadableGolden_ReturnsSanitizedCorpusRejected()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.Copy(GoldenPath(), path, overwrite: true);
            using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            AssertCliFailure(path, VisualEvaluationCli.CorpusRejectedError);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void CorpusAndGolden_UseExactSchemasAndPrivacySafeShapes()
    {
        using var corpus = JsonDocument.Parse(File.ReadAllBytes(CorpusPath()));
        using var golden = JsonDocument.Parse(File.ReadAllBytes(GoldenPath()));

        AssertCorpusSchema(corpus.RootElement);
        AssertGoldenSchema(golden.RootElement);
        AssertDeniedVocabularyAndShapes(corpus.RootElement);
        AssertDeniedVocabularyAndShapes(golden.RootElement);
        Assert.DoesNotContain("highlightMatchPpm", golden.RootElement.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("nonBlackPpm", golden.RootElement.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("meanLumaPpm", golden.RootElement.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void CorpusAndGolden_ExerciseTruthClassesMetricsFalseMatchAndNullableRatios()
    {
        using var corpus = JsonDocument.Parse(File.ReadAllBytes(CorpusPath()));
        using var golden = JsonDocument.Parse(File.ReadAllBytes(GoldenPath()));
        var truths = corpus.RootElement.GetProperty("suites")[0]
            .GetProperty("scenarios")
            .EnumerateArray()
            .SelectMany(scenario => scenario.GetProperty("segments").EnumerateArray())
            .Select(segment => segment.GetProperty("truth").GetString())
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(
            ["microphoneExcluded", "remoteActivity", "remoteInactive", "remoteUnavailable"],
            truths.Order(StringComparer.Ordinal));

        var candidate = golden.RootElement.GetProperty("suites")[0].GetProperty("candidates")[0];
        var metrics = candidate.GetProperty("metrics");
        foreach (var property in new[]
                 {
                     "match", "miss", "falseMatch", "abstain", "unavailable", "insufficient", "microphoneExcluded"
                 })
            Assert.True(metrics.GetProperty(property).GetInt64() > 0, $"Expected {property} to be exercised.");
        Assert.True(candidate.GetProperty("latencyMs").GetProperty("sampleCount").GetInt32() > 0);
        Assert.Contains(
            candidate.GetProperty("scenarios").EnumerateArray(),
            scenario => scenario.GetProperty("ratios").EnumerateObject()
                .Any(property => property.Value.ValueKind == JsonValueKind.Null));
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void Golden_ExercisesHoldCoverageDiscontinuityAndDelayedClosureBoundaries()
    {
        using var golden = JsonDocument.Parse(File.ReadAllBytes(GoldenPath()));
        var scenarios = golden.RootElement.GetProperty("suites")[0]
            .GetProperty("candidates")[0]
            .GetProperty("scenarios")
            .EnumerateArray()
            .ToDictionary(
                scenario => scenario.GetProperty("scenarioId").GetString()!,
                scenario => scenario,
                StringComparer.Ordinal);

        Assert.Equal(
            Enumerable.Range(1, 16).Select(index => $"scenario-{index:000}"),
            scenarios.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(1, Metric(scenarios, "scenario-004", "unavailable"));
        Assert.Equal(1, Metric(scenarios, "scenario-005", "unavailable"));
        Assert.Equal(1, Metric(scenarios, "scenario-006", "unavailable"));
        Assert.Equal(1, Metric(scenarios, "scenario-007", "abstain"));
        Assert.Equal(1, Metric(scenarios, "scenario-008", "abstain"));
        Assert.Equal(1, Metric(scenarios, "scenario-009", "match"));
        Assert.Equal(1, Metric(scenarios, "scenario-010", "miss"));
        Assert.Equal(1, Metric(scenarios, "scenario-011", "match"));
        Assert.Equal(1, Metric(scenarios, "scenario-012", "falseMatch"));
        Assert.Equal(1, Metric(scenarios, "scenario-013", "match"));
        Assert.Equal(1, Metric(scenarios, "scenario-014", "abstain"));
        Assert.Equal(1, Metric(scenarios, "scenario-015", "match"));
        Assert.Equal(1, Metric(scenarios, "scenario-015", "miss"));
        Assert.Equal(7_000, scenarios["scenario-016"].GetProperty("latencyMs").GetProperty("p50Ms").GetInt64());
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void ProductionDefaultsAndPublishContract_RemainUnchanged()
    {
        var meet = VisualProbeProfiles.GetProduction(MeetingProvider.GoogleMeet);
        var teams = VisualProbeProfiles.GetProduction(MeetingProvider.MicrosoftTeams);
        Assert.Equal(VisualProbeProfileValidationState.Unvalidated, meet.ValidationState);
        Assert.Equal(VisualProbeProfileValidationState.Unvalidated, teams.ValidationState);
        Assert.Equal(0, meet.ExpectedFeatureCount);
        Assert.Equal(0, teams.ExpectedFeatureCount);
        Assert.Null(meet.DetectionPolicy);
        Assert.Null(teams.DetectionPolicy);
        Assert.Equal("No disponible", AnonymousVisualEvidenceViewModel.Unavailable.StatusText);

        var publishContract = File.ReadAllText(Path.Combine(RepositoryRoot(), "installer", "publish.ps1"));
        Assert.Contains("\"Trazio.AsistenteReunion.VisualAnalysis.dll\"", publishContract, StringComparison.Ordinal);
        Assert.Contains("$visualAnalysisVersion", publishContract, StringComparison.Ordinal);
        Assert.Contains("\"Trazio.AsistenteReunion.VisualEvaluation.exe\"", publishContract, StringComparison.Ordinal);
        Assert.Contains("\"Trazio.AsistenteReunion.VisualEvaluation.dll\"", publishContract, StringComparison.Ordinal);
        Assert.Contains("\"Trazio.AsistenteReunion.VisualEvaluation.deps.json\"", publishContract, StringComparison.Ordinal);
        Assert.Contains("\"Trazio.AsistenteReunion.VisualEvaluation.runtimeconfig.json\"", publishContract, StringComparison.Ordinal);
        Assert.Contains("\"synthetic-corpus-v1.json\"", publishContract, StringComparison.Ordinal);
        Assert.Contains("\"synthetic-corpus-v1.golden.json\"", publishContract, StringComparison.Ordinal);
        Assert.Contains("\"tools\"", publishContract, StringComparison.Ordinal);
        Assert.Contains("\"evaluation\"", publishContract, StringComparison.Ordinal);
        Assert.Contains("$prohibitedEvaluatorFiles.Contains($_.Name)", publishContract, StringComparison.Ordinal);
        Assert.Contains("$prohibitedEvaluationDataFiles.Contains($_.Name)", publishContract, StringComparison.Ordinal);
        Assert.Contains("$prohibitedDirectoryNames.Contains($_.Name)", publishContract, StringComparison.Ordinal);
        Assert.Contains("Get-ChildItem -LiteralPath $publishOutput -Recurse -Force", publishContract, StringComparison.Ordinal);
        var appProject = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "src",
            "Trazio.AsistenteReunion.App",
            "Trazio.AsistenteReunion.App.csproj"));
        Assert.DoesNotContain("VisualEvaluation", appProject, StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] EvaluateCanonicalBytes()
    {
        var corpus = VisualEvaluationCorpusLoader.Load(CorpusPath());
        var report = new VisualEvaluationRunner().Evaluate(corpus);
        return Encoding.UTF8.GetBytes(VisualEvaluationReportSerializer.Serialize(report));
    }

    private static void AssertGoldenMismatch(byte[] contents)
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, contents);
            var before = File.ReadAllBytes(path);

            AssertCliFailure(path, VisualEvaluationCli.GoldenMismatchError);
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void AssertCliFailure(string goldenPath, string expectedError)
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = VisualEvaluationCli.Run(
            ["verify", "--corpus", CorpusPath(), "--golden", goldenPath],
            output,
            error);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Equal(expectedError + Environment.NewLine, error.ToString());
        Assert.DoesNotContain(goldenPath, error.ToString(), StringComparison.Ordinal);
    }

    private static void AssertCorpusSchema(JsonElement root)
    {
        AssertProperties(root, "schemaVersion", "corpusId", "scope", "suites");
        foreach (var suite in root.GetProperty("suites").EnumerateArray())
        {
            AssertProperties(suite, "id", "provider", "profileVersion", "evidenceVersion", "detectorVersion", "expectedFeatureCount", "candidates", "scenarios");
            foreach (var candidate in suite.GetProperty("candidates").EnumerateArray())
            {
                AssertProperties(candidate, "id", "policyVersion", "detector", "correlation");
                AssertProperties(candidate.GetProperty("detector"), "enterScore", "exitScore", "activationHoldMs", "releaseHoldMs", "maxObservationGapMs", "minimumCoherentFeatures");
                AssertProperties(candidate.GetProperty("correlation"), "minimumConfidencePpm", "minimumCoveragePpm", "minimumActivityOverlapPpm");
            }

            foreach (var scenario in suite.GetProperty("scenarios").EnumerateArray())
            {
                AssertProperties(scenario, "id", "observations", "completionAtMs", "segments");
                foreach (var observation in scenario.GetProperty("observations").EnumerateArray())
                {
                    AssertProperties(observation, "atMs", "surfaceRevision", "status", "features");
                    foreach (var feature in observation.GetProperty("features").EnumerateArray())
                        AssertProperties(feature, "highlightMatchPpm", "nonBlackPpm", "meanLumaPpm");
                }

                foreach (var segment in scenario.GetProperty("segments").EnumerateArray())
                    AssertProperties(segment, "source", "startMs", "endMs", "truth");
            }
        }
    }

    private static void AssertGoldenSchema(JsonElement root)
    {
        AssertProperties(root, "schemaVersion", "corpusId", "scope", "suites");
        foreach (var suite in root.GetProperty("suites").EnumerateArray())
        {
            AssertProperties(suite, "suiteId", "provider", "profileVersion", "evidenceVersion", "detectorVersion", "expectedFeatureCount", "candidates");
            foreach (var candidate in suite.GetProperty("candidates").EnumerateArray())
            {
                AssertProperties(candidate, "candidateId", "policyVersion", "metrics", "denominators", "ratios", "latencyMs", "resources", "scenarios");
                AssertReportValues(candidate);
                foreach (var scenario in candidate.GetProperty("scenarios").EnumerateArray())
                {
                    AssertProperties(scenario, "scenarioId", "metrics", "denominators", "ratios", "latencyMs", "resources");
                    AssertReportValues(scenario);
                }
            }
        }
    }

    private static void AssertReportValues(JsonElement value)
    {
        AssertProperties(value.GetProperty("metrics"), "match", "miss", "falseMatch", "abstain", "unavailable", "insufficient", "microphoneExcluded");
        AssertProperties(value.GetProperty("denominators"), "total", "evaluated", "remoteActivity", "remoteInactive", "remoteUnavailable");
        AssertProperties(value.GetProperty("ratios"), "matchPpm", "missPpm", "falseMatchPpm", "abstainPpm", "unavailablePpm", "insufficientPpm", "microphoneExcludedPpm");
        AssertProperties(value.GetProperty("latencyMs"), "sampleCount", "p50Ms", "p95Ms", "maxMs");
        AssertProperties(value.GetProperty("resources"), "detectorCalls", "observations", "featureCells", "correlationCalls", "evidenceIntervals", "peakRetainedIntervals", "maxFeatures");
    }

    private static void AssertDeniedVocabularyAndShapes(JsonElement value)
    {
        var forbidden = new[]
        {
            "pixel", "coordinate", "rgb", "bgra", "image", "screenshot", "ocr", "name", "transcript",
            "url", "hwnd", "pid", "title", "process", "dom", "speaker", "sessionid", "timestamp", "description"
        };
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                {
                    Assert.DoesNotContain(forbidden, token =>
                        property.Name.Contains(token, StringComparison.OrdinalIgnoreCase));
                    AssertDeniedVocabularyAndShapes(property.Value);
                }
                break;
            case JsonValueKind.Array:
                Assert.DoesNotContain(value.EnumerateArray(), item => item.ValueKind == JsonValueKind.Number);
                foreach (var item in value.EnumerateArray()) AssertDeniedVocabularyAndShapes(item);
                break;
            case JsonValueKind.String:
                var text = value.GetString()!;
                Assert.DoesNotContain(forbidden, token => text.Contains(token, StringComparison.OrdinalIgnoreCase));
                break;
        }
    }

    private static void AssertProperties(JsonElement element, params string[] expected) =>
        Assert.Equal(
            expected.Order(StringComparer.Ordinal),
            element.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));

    private static long Metric(
        IReadOnlyDictionary<string, JsonElement> scenarios,
        string scenarioId,
        string metric) => scenarios[scenarioId].GetProperty("metrics").GetProperty(metric).GetInt64();

    private static string CorpusPath() => Path.Combine(
        RepositoryRoot(), "evaluation", "stage-7b", "synthetic-corpus-v1.json");

    private static string GoldenPath() => Path.Combine(
        RepositoryRoot(), "evaluation", "stage-7b", "synthetic-corpus-v1.golden.json");

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Trazio.AsistenteReunion.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
