using System.Text.Json;
using Trazio.AsistenteReunion.Core;
using Trazio.AsistenteReunion.VisualEvaluation;

namespace Trazio.AsistenteReunion.Tests;

public sealed class VisualEvaluationCorpusTests
{
    [Fact]
    [Trait("Area", "VisualCapture")]
    public void LoadJson_ValidSyntheticAggregateCorpus_LoadsStrictModel()
    {
        var corpus = VisualEvaluationCorpusLoader.LoadJson(ValidCorpusJson());

        Assert.Equal(1, corpus.SchemaVersion);
        Assert.Equal("corpus-001", corpus.CorpusId);
        Assert.Equal(VisualEvaluationCorpus.RequiredScope, corpus.Scope);
        var suite = Assert.Single(corpus.Suites);
        Assert.Equal("google-meet", suite.Provider);
        Assert.Equal(2, suite.ExpectedFeatureCount);
        Assert.Equal(912_345, suite.Scenarios[0].Observations[0].Features[0].HighlightMatchPpm);
    }

    [Theory]
    [Trait("Area", "VisualCapture")]
    [InlineData("\"schemaVersion\":1", "\"unexpected\":true,\"schemaVersion\":1")]
    [InlineData("\"enterScore\":800", "\"unexpected\":true,\"enterScore\":800")]
    public void LoadJson_UnknownMemberAtAnyLevel_Rejects(
        string original,
        string replacement)
    {
        var json = ValidCorpusJson()
            .Replace(original, replacement, StringComparison.Ordinal);

        Assert.Throws<JsonException>(() => VisualEvaluationCorpusLoader.LoadJson(json));
    }

    [Theory]
    [Trait("Area", "VisualCapture")]
    [InlineData("\"schemaVersion\":1", "\"schemaVersion\":2")]
    [InlineData("\"scope\":\"synthetic-aggregate-only\"", "\"scope\":\"production\"")]
    [InlineData("\"corpusId\":\"corpus-001\"", "\"corpusId\":\"Meeting With Alice\"")]
    [InlineData("\"corpusId\":\"corpus-001\"", "\"corpusId\":\"suite-001\"")]
    [InlineData("\"id\":\"candidate-001\"", "\"id\":\"candidate-alpha\"")]
    [InlineData("\"provider\":\"google-meet\"", "\"provider\":\"GoogleMeet\"")]
    [InlineData("\"exitScore\":200", "\"exitScore\":800")]
    [InlineData("\"minimumCoherentFeatures\":2", "\"minimumCoherentFeatures\":3")]
    [InlineData("\"minimumConfidencePpm\":750000", "\"minimumConfidencePpm\":1000001")]
    [InlineData("\"expectedFeatureCount\":2", "\"expectedFeatureCount\":3")]
    [InlineData("\"expectedFeatureCount\":2", "\"expectedFeatureCount\":17")]
    [InlineData("\"completionAtMs\":3000", "\"completionAtMs\":999")]
    [InlineData("\"completionAtMs\":3000", "\"completionAtMs\":9223372036854775807")]
    [InlineData("\"suites\":[{", "\"suites\":[null,{")]
    public void LoadJson_InvalidContract_Rejects(string original, string replacement)
    {
        var json = ValidCorpusJson().Replace(original, replacement, StringComparison.Ordinal);

        Assert.Throws<VisualEvaluationCorpusException>(() => VisualEvaluationCorpusLoader.LoadJson(json));
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void LoadJson_NonIncreasingObservations_Rejects()
    {
        var json = ValidCorpusJson().Replace("\"atMs\":1000", "\"atMs\":0", StringComparison.Ordinal);

        Assert.Throws<VisualEvaluationCorpusException>(() => VisualEvaluationCorpusLoader.LoadJson(json));
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void LoadJson_DuplicateMember_Rejects()
    {
        var json = ValidCorpusJson().Replace(
            "\"schemaVersion\":1",
            "\"schemaVersion\":1,\"schemaVersion\":1",
            StringComparison.Ordinal);

        Assert.Throws<JsonException>(() => VisualEvaluationCorpusLoader.LoadJson(json));
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void LoadJson_UnavailableObservationWithFeatures_Rejects()
    {
        var json = ValidCorpusJson().Replace(
            "\"status\":\"available\",\"features\":[{\"highlightMatchPpm\":100000,\"nonBlackPpm\":900000,\"meanLumaPpm\":100000},{\"highlightMatchPpm\":100000,\"nonBlackPpm\":900000,\"meanLumaPpm\":100000}]",
            "\"status\":\"unavailable\",\"features\":[{\"highlightMatchPpm\":100000,\"nonBlackPpm\":900000,\"meanLumaPpm\":100000}]",
            StringComparison.Ordinal);

        Assert.Throws<VisualEvaluationCorpusException>(() => VisualEvaluationCorpusLoader.LoadJson(json));
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void LoadJson_MicrophoneTruthOnSystemOutput_Rejects()
    {
        var json = ValidCorpusJson().Replace(
            "\"truth\":\"remoteActivity\"",
            "\"truth\":\"microphoneExcluded\"",
            StringComparison.Ordinal);

        Assert.Throws<VisualEvaluationCorpusException>(() => VisualEvaluationCorpusLoader.LoadJson(json));
    }

    [Theory]
    [Trait("Area", "VisualCapture")]
    [InlineData("\"status\":\"available\"", "\"status\":\"Available\"")]
    [InlineData("\"source\":\"systemOutput\"", "\"source\":\"SystemOutput\"")]
    [InlineData("\"truth\":\"remoteActivity\"", "\"truth\":\"RemoteActivity\"")]
    public void LoadJson_NonCanonicalEnumToken_Rejects(string original, string replacement)
    {
        var json = ValidCorpusJson().Replace(original, replacement, StringComparison.Ordinal);

        Assert.Throws<JsonException>(() => VisualEvaluationCorpusLoader.LoadJson(json));
    }

    [Theory]
    [Trait("Area", "VisualCapture")]
    [InlineData("\"profileVersion\":1", "\"profileVersion\":999")]
    [InlineData("\"evidenceVersion\":1", "\"evidenceVersion\":999")]
    [InlineData("\"detectorVersion\":1", "\"detectorVersion\":999")]
    [InlineData("\"policyVersion\":1", "\"policyVersion\":999")]
    public void LoadJson_VersionNotBoundToCurrentImplementation_Rejects(string original, string replacement)
    {
        var json = ValidCorpusJson().Replace(original, replacement, StringComparison.Ordinal);

        Assert.Throws<VisualEvaluationCorpusException>(() => VisualEvaluationCorpusLoader.LoadJson(json));
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void LoadJson_JsonDepthAboveBound_Rejects()
    {
        var nested = new string('[', VisualEvaluationLimits.MaximumJsonDepth + 1) +
            "0" +
            new string(']', VisualEvaluationLimits.MaximumJsonDepth + 1);
        var json = ValidCorpusJson().Replace("\"schemaVersion\":1", $"\"schemaVersion\":{nested}", StringComparison.Ordinal);

        Assert.Throws<JsonException>(() => VisualEvaluationCorpusLoader.LoadJson(json));
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void LoadJson_ByteSizeAboveBound_RejectsBeforeParsing()
    {
        var oversized = new string(' ', VisualEvaluationLimits.MaximumCorpusBytes + 1);

        Assert.Throws<VisualEvaluationCorpusException>(() => VisualEvaluationCorpusLoader.LoadJson(oversized));
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void Load_FileByteSizeAboveBound_RejectsBeforeBufferAllocation()
    {
        var path = Path.GetTempFileName();
        try
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
                stream.SetLength(VisualEvaluationLimits.MaximumCorpusBytes + 1L);

            Assert.Throws<VisualEvaluationCorpusException>(() => VisualEvaluationCorpusLoader.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void LoadJson_CurrentProvenanceVersions_AreAccepted()
    {
        var suite = Assert.Single(VisualEvaluationCorpusLoader.LoadJson(ValidCorpusJson()).Suites);
        var candidate = Assert.Single(suite.Candidates);

        Assert.Equal(AnonymousVisualEvidenceVersions.CurrentProfile, suite.ProfileVersion);
        Assert.Equal(AnonymousVisualEvidenceVersions.CurrentEvidence, suite.EvidenceVersion);
        Assert.Equal(AnonymousVisualEvidenceVersions.CurrentDetector, suite.DetectorVersion);
        Assert.Equal(AnonymousVisualEvidenceVersions.CurrentPolicy, candidate.PolicyVersion);
    }

    internal static string ValidCorpusJson() => """
        {
          "schemaVersion":1,
          "corpusId":"corpus-001",
          "scope":"synthetic-aggregate-only",
          "suites":[{
            "id":"suite-001",
            "provider":"google-meet",
            "profileVersion":1,
            "evidenceVersion":1,
            "detectorVersion":1,
            "expectedFeatureCount":2,
            "candidates":[{
              "id":"candidate-001",
              "policyVersion":1,
              "detector":{
                "enterScore":800,
                "exitScore":200,
                "activationHoldMs":0,
                "releaseHoldMs":0,
                "maxObservationGapMs":2000,
                "minimumCoherentFeatures":2
              },
              "correlation":{
                "minimumConfidencePpm":750000,
                "minimumCoveragePpm":800000,
                "minimumActivityOverlapPpm":250000
              }
            }],
            "scenarios":[{
              "id":"scenario-001",
              "observations":[
                {"atMs":0,"surfaceRevision":1,"status":"available","features":[{"highlightMatchPpm":912345,"nonBlackPpm":900000,"meanLumaPpm":123456},{"highlightMatchPpm":900000,"nonBlackPpm":900000,"meanLumaPpm":900000}]},
                {"atMs":1000,"surfaceRevision":1,"status":"available","features":[{"highlightMatchPpm":900000,"nonBlackPpm":900000,"meanLumaPpm":900000},{"highlightMatchPpm":900000,"nonBlackPpm":900000,"meanLumaPpm":900000}]},
                {"atMs":2000,"surfaceRevision":1,"status":"available","features":[{"highlightMatchPpm":100000,"nonBlackPpm":900000,"meanLumaPpm":100000},{"highlightMatchPpm":100000,"nonBlackPpm":900000,"meanLumaPpm":100000}]}
              ],
              "completionAtMs":3000,
              "segments":[
                {"source":"systemOutput","startMs":0,"endMs":2000,"truth":"remoteActivity"},
                {"source":"systemOutput","startMs":2000,"endMs":3000,"truth":"remoteInactive"},
                {"source":"microphone","startMs":0,"endMs":1000,"truth":"microphoneExcluded"}
              ]
            }]
          }]
        }
        """;
}
