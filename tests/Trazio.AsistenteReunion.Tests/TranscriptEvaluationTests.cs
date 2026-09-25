using System.Security.Cryptography;
using System.Text;
using Trazio.AsistenteReunion.TranscriptEvaluation;

namespace Trazio.AsistenteReunion.Tests;

public sealed class TranscriptEvaluationTests
{
    [Fact]
    public void Normalize_SpanishPunctuationAndCase_KeepsMeaningfulLetters()
    {
        Assert.Equal("sí no reunión 25", TranscriptMetrics.Normalize("¡SÍ, no! Reunión: 25."));
        Assert.Equal((0, 4, 0, 13), TranscriptMetrics.CountErrors("Sí, no reunión 25", "sí no reunión 25"));
    }

    [Fact]
    public void Evaluate_NegationError_RemainsSeparateFromLegibility()
    {
        using var fixture = new Fixture();
        var manifest = fixture.CreateManifest();
        manifest = manifest with
        {
            Hypotheses =
            [
                manifest.Hypotheses[0] with
                {
                    Text = "sí acepto la oferta",
                    MeaningReview = new HumanMeaningReview(true, ["negation"], "reviewer-1", fixture.Now)
                }
            ]
        };
        var result = EvaluationRunner.Evaluate(manifest, fixture.Root, fixture.Now);
        Assert.Equal(1, result.Configurations[0].MeaningErrorCount);
        Assert.Equal(1, result.Configurations[0].MeaningErrorKinds["negation"]);
        Assert.True(result.Configurations[0].Wer > 0);
        Assert.Equal(120, result.Configurations[0].LatencyP95Ms);
    }

    [Fact]
    public void Evaluate_MissingOrExpiredConsent_Rejects()
    {
        using var fixture = new Fixture();
        var manifest = fixture.CreateManifest();
        Assert.Throws<EvaluationException>(() => EvaluationRunner.Evaluate(
            manifest with { Consent = manifest.Consent with { Scope = "other" } }, fixture.Root, fixture.Now));
        Assert.Throws<EvaluationException>(() => EvaluationRunner.Evaluate(
            manifest with { Consent = manifest.Consent with { ExpiresAtUtc = fixture.Now } }, fixture.Root, fixture.Now));
        Assert.Throws<EvaluationException>(() => EvaluationRunner.Evaluate(
            manifest with { Consent = manifest.Consent with { EvidenceSha256 = new string('0', 64) } }, fixture.Root, fixture.Now));
    }

    [Fact]
    public void Evaluate_PathTraversalAndAudioTampering_Rejects()
    {
        using var fixture = new Fixture();
        var manifest = fixture.CreateManifest();
        Assert.Throws<EvaluationException>(() => EvaluationRunner.Evaluate(
            manifest with { Clips = [manifest.Clips[0] with { AudioRelativePath = "../other.wav" }] },
            fixture.Root, fixture.Now));
        Assert.Throws<EvaluationException>(() => EvaluationRunner.Evaluate(
            manifest with { Clips = [manifest.Clips[0] with { AudioSha256 = new string('0', 64) }] },
            fixture.Root, fixture.Now));
    }

    [Fact]
    public void Evaluate_DuplicateOrIncompleteMatrix_Rejects()
    {
        using var fixture = new Fixture();
        var manifest = fixture.CreateManifest();
        Assert.Throws<EvaluationException>(() => EvaluationRunner.Evaluate(
            manifest with { Hypotheses = [manifest.Hypotheses[0], manifest.Hypotheses[0]] },
            fixture.Root, fixture.Now));
        Assert.Throws<EvaluationException>(() => EvaluationRunner.Evaluate(
            manifest with { Hypotheses = [] }, fixture.Root, fixture.Now));
    }

    [Fact]
    public void Evaluate_NonHumanSemanticLabel_Rejects()
    {
        using var fixture = new Fixture();
        var manifest = fixture.CreateManifest();
        Assert.Throws<EvaluationException>(() => EvaluationRunner.Evaluate(
            manifest with { Hypotheses = [manifest.Hypotheses[0] with
                { MeaningReview = new HumanMeaningReview(true, [], "", fixture.Now) }] },
            fixture.Root, fixture.Now));
    }

    [Fact]
    public void Percentile_InterpolatesMeasuredSamples()
    {
        Assert.Equal(2.5, TranscriptMetrics.Percentile([1, 2, 3, 4], .5));
        Assert.Equal(3.85, TranscriptMetrics.Percentile([1, 2, 3, 4], .95), 10);
    }

    [Fact]
    public void CountErrors_OversizedTextOrComparison_RejectsBeforeScoring()
    {
        Assert.Throws<EvaluationException>(() => TranscriptMetrics.CountErrors(new string('a', 4_097), "a"));
        Assert.Throws<EvaluationException>(() => TranscriptMetrics.CountErrors(new string('a', 1_001), new string('b', 1_001)));
    }

    [Fact]
    public void Evaluate_AggregateComparisonBudget_RejectsBeforeScoring()
    {
        using var fixture = new Fixture();
        var original = fixture.CreateManifest();
        var clip = original.Clips[0] with { ApprovedReferenceText = new string('a', 1_000) };
        var configurations = Enumerable.Range(0, 26)
            .Select(i => original.Configurations[0] with { ConfigurationId = $"config-{i}" }).ToArray();
        var hypotheses = configurations
            .Select(config => original.Hypotheses[0] with
            {
                ConfigurationId = config.ConfigurationId,
                Text = new string('b', 1_000)
            }).ToArray();
        Assert.Throws<EvaluationException>(() => EvaluationRunner.Evaluate(
            original with { Clips = [clip], Configurations = configurations, Hypotheses = hypotheses },
            fixture.Root, fixture.Now));
    }

    [Theory]
    [InlineData("Negation")]
    [InlineData("negación")]
    [InlineData("unrecognized")]
    public void Evaluate_NonCanonicalMeaningErrorKind_Rejects(string kind)
    {
        using var fixture = new Fixture();
        var manifest = fixture.CreateManifest();
        var hypothesis = manifest.Hypotheses[0] with
        {
            MeaningReview = new HumanMeaningReview(true, [kind], "reviewer-1", fixture.Now)
        };
        Assert.Throws<EvaluationException>(() => EvaluationRunner.Evaluate(
            manifest with { Hypotheses = [hypothesis] }, fixture.Root, fixture.Now));
    }

    [Fact]
    public void Evaluate_UnknownVocabularyVersionOrDuplicateKind_Rejects()
    {
        using var fixture = new Fixture();
        var manifest = fixture.CreateManifest();
        Assert.Throws<EvaluationException>(() => EvaluationRunner.Evaluate(
            manifest with { MeaningErrorKindsVersion = "stage9-meaning-errors-v2" },
            fixture.Root, fixture.Now));
        var duplicate = manifest.Hypotheses[0] with
        {
            MeaningReview = new HumanMeaningReview(true, ["negation", "negation"], "reviewer-1", fixture.Now)
        };
        Assert.Throws<EvaluationException>(() => EvaluationRunner.Evaluate(
            manifest with { Hypotheses = [duplicate] }, fixture.Root, fixture.Now));
    }

    [Fact]
    public void Run_MissingPrivateFile_DoesNotLeakPathInError()
    {
        using var fixture = new Fixture();
        using var stderr = new StringWriter();
        using var stdout = new StringWriter();
        var sensitivePath = Path.Combine(fixture.Root, "secret-client-name.evaluation.private.json");
        var outputPath = Path.Combine(fixture.Root, "report.evaluation.private.json");
        Assert.Equal(1, Program.Run(["--manifest", sensitivePath, "--output", outputPath], stderr, stdout));
        Assert.DoesNotContain("secret-client-name", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("inaccesible", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_ReparsePointInAncestor_RejectsWhenPlatformSupportsSymlinks()
    {
        using var fixture = new Fixture();
        var manifest = fixture.CreateManifest();
        var nested = Path.Combine(fixture.Root, "nested");
        Directory.CreateDirectory(nested);
        File.Copy(Path.Combine(fixture.Root, "consent.txt"), Path.Combine(nested, "consent.txt"));
        File.Copy(Path.Combine(fixture.Root, "clip.wav"), Path.Combine(nested, "clip.wav"));
        var alias = Path.Combine(Path.GetTempPath(), "trazio-evaluation-link-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateSymbolicLink(alias, fixture.Root);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }
        try
        {
            Assert.Throws<EvaluationException>(() => EvaluationRunner.Evaluate(
                manifest, Path.Combine(alias, "nested"), fixture.Now));
            var manifestPath = Path.Combine(nested, "sample.evaluation.private.json");
            File.WriteAllText(manifestPath,
                System.Text.Json.JsonSerializer.Serialize(manifest, EvaluationJson.Options));
            Assert.Throws<EvaluationException>(() => EvaluationRunner.EvaluateFile(
                Path.Combine(alias, "nested", "sample.evaluation.private.json"), fixture.Now));
        }
        finally
        {
            Directory.Delete(alias);
        }
    }

    private sealed class Fixture : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "trazio-evaluation-test-" + Guid.NewGuid().ToString("N"));
        public readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

        public Fixture() => Directory.CreateDirectory(Root);

        public EvaluationManifest CreateManifest()
        {
            var evidence = Encoding.UTF8.GetBytes("Consentimiento ficticio para prueba sintética.");
            var audio = new byte[] { 82, 73, 70, 70, 4, 0, 0, 0, 87, 65, 86, 69 };
            File.WriteAllBytes(Path.Combine(Root, "consent.txt"), evidence);
            File.WriteAllBytes(Path.Combine(Root, "clip.wav"), audio);
            var consent = new ConsentRecord("synthetic-consent", EvaluationRunner.RequiredConsentScope,
                Now.AddDays(-1), Now.AddDays(1), "consent.txt", Convert.ToHexString(SHA256.HashData(evidence)));
            var clip = new EvaluationClip("clip-1", "clip.wav", Convert.ToHexString(SHA256.HashData(audio)),
                consent.ConsentId, "no acepto la oferta", "reviewer-1", Now.AddHours(-1), ["negation", "normal"]);
            var configuration = new EvaluationConfiguration("whisper-base", "acoustic", "whisper-base",
                new string('A', 64), new string('B', 64), "test-cpu");
            var hypothesis = new EvaluationHypothesis("clip-1", configuration.ConfigurationId,
                "no acepto la oferta", 120, 1_048_576,
                new HumanMeaningReview(false, [], "reviewer-1", Now));
            return new EvaluationManifest(1, MeaningErrorVocabulary.Version, consent, [clip], [configuration], [hypothesis]);
        }

        public void Dispose() => Directory.Delete(Root, true);
    }
}
