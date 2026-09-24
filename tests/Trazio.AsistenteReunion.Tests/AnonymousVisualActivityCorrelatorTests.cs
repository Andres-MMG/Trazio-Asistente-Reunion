using Trazio.AsistenteReunion.App;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.Tests;

public sealed class AnonymousVisualActivityCorrelatorTests
{
    private static readonly Guid CoverageId = Guid.ParseExact("11111111111111111111111111111111", "N");
    private static readonly Guid ActivityId = Guid.ParseExact("22222222222222222222222222222222", "N");
    private static readonly AnonymousVisualEvidenceProvenance Provenance = new(
        MeetingProvider.GoogleMeet,
        ProfileVersion: 1,
        EvidenceVersion: 1,
        DetectorVersion: 1,
        PolicyVersion: 1);

    [Fact]
    public void EvidenceContract_ExposesNoIdentityTextWindowOrPixelData()
    {
        var propertyNames = typeof(AnonymousVisualEvidenceInterval)
            .GetProperties()
            .Select(property => property.Name.ToLowerInvariant())
            .ToArray();
        var forbiddenNames = new[]
        {
            "name", "ocr", "text", "roi", "coordinate", "hwnd", "pid", "url", "title", "image", "pixel", "buffer"
        };

        Assert.DoesNotContain(propertyNames, propertyName =>
            forbiddenNames.Any(forbidden => propertyName.Contains(forbidden, StringComparison.Ordinal)));
        Assert.DoesNotContain(typeof(AnonymousVisualEvidenceInterval).GetProperties(), property =>
            property.PropertyType == typeof(byte[]) ||
            property.PropertyType == typeof(Memory<byte>) ||
            property.PropertyType == typeof(ReadOnlyMemory<byte>));
    }

    [Fact]
    public void EvidenceInterval_NonPositiveProfileVersion_IsRejected()
    {
        var invalid = Provenance with { ProfileVersion = 0 };

        Assert.Throws<ArgumentOutOfRangeException>(() => AnonymousVisualEvidenceInterval.Activity(
            ActivityId,
            "session",
            TimeSpan.Zero,
            TimeSpan.FromSeconds(1),
            1,
            invalid));
    }

    [Fact]
    public void Correlator_NonPositiveSupportedProfileVersion_IsRejected()
    {
        var invalid = new AnonymousVisualCorrelationPolicy(
            MeetingProvider.GoogleMeet,
            ProfileVersion: 0,
            EvidenceVersion: 1,
            DetectorVersion: 1,
            PolicyVersion: 1,
            MinimumConfidence: 0.75,
            MinimumCoverageRatio: 0.8,
            MinimumActivityOverlapRatio: 0.25);

        Assert.Throws<ArgumentOutOfRangeException>(() => new AnonymousVisualActivityCorrelator(invalid));
    }

    [Fact]
    public void Correlate_SystemOutputWithSufficientCoverageAndActivity_ReturnsMatch()
    {
        var segment = Segment(AudioSourceKind.SystemOutput);
        var evidence = Loaded(
            Coverage(TimeSpan.Zero, TimeSpan.FromSeconds(10)),
            Activity(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(8)));

        var result = Correlator().Correlate(segment, evidence);

        Assert.Equal(AnonymousVisualCorrelationOutcome.Matched, result.Outcome);
        Assert.Equal(AnonymousVisualCorrelationReason.None, result.Reason);
        Assert.True(result.HasEvidence);
        Assert.Equal(1, result.CoverageRatio, 6);
        Assert.Equal(0.6, result.ActivityOverlapRatio, 6);
    }

    [Fact]
    public void Correlate_CoreFacadeAndSharedEngine_ReturnIdenticalResult()
    {
        var segment = Segment(AudioSourceKind.SystemOutput);
        var evidence = Loaded(
            Coverage(TimeSpan.Zero, TimeSpan.FromSeconds(10)),
            Activity(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(8)));
        var policy = Policy();

        var facadeResult = new AnonymousVisualActivityCorrelator(policy).Correlate(segment, evidence);
        var engineResult = new AnonymousVisualCorrelationEngine(policy).Correlate(
            new AnonymousVisualCorrelationSegment(segment.SessionId, segment.Source, segment.Start, segment.End),
            evidence.SessionId,
            evidence.Status,
            evidence.Intervals);

        Assert.Equal(facadeResult, engineResult);
    }

    [Fact]
    public void Correlate_MicrophoneWithIdenticalOverlap_AlwaysAbstainsWithoutEvidence()
    {
        var segment = Segment(AudioSourceKind.Microphone) with { SpeakerName = "Local speaker" };
        var evidence = Loaded(
            Coverage(TimeSpan.Zero, TimeSpan.FromSeconds(10)),
            Activity(TimeSpan.Zero, TimeSpan.FromSeconds(10)));

        var result = Correlator().Correlate(segment, evidence);

        Assert.Equal(AnonymousVisualCorrelationOutcome.Abstained, result.Outcome);
        Assert.Equal(AnonymousVisualCorrelationReason.UnsupportedSource, result.Reason);
        Assert.False(result.HasEvidence);
        Assert.Equal("Local speaker", segment.SpeakerName);
    }

    [Fact]
    public void Correlate_SufficientCoverageWithoutActivity_ReturnsInsufficientEvidence()
    {
        var result = Correlator().Correlate(
            Segment(AudioSourceKind.SystemOutput),
            Loaded(Coverage(TimeSpan.Zero, TimeSpan.FromSeconds(10))));

        Assert.Equal(AnonymousVisualCorrelationOutcome.InsufficientEvidence, result.Outcome);
        Assert.Equal(AnonymousVisualCorrelationReason.ActivityOverlapBelowMinimum, result.Reason);
        Assert.False(result.HasEvidence);
    }

    [Fact]
    public void Correlate_NoCoverage_ReturnsUnavailable()
    {
        var result = Correlator().Correlate(
            Segment(AudioSourceKind.SystemOutput),
            AnonymousVisualEvidenceReadResult.Loaded("session", []));

        Assert.Equal(AnonymousVisualCorrelationOutcome.Unavailable, result.Outcome);
        Assert.Equal(AnonymousVisualCorrelationReason.NoAvailableCoverage, result.Reason);
    }

    [Fact]
    public void Correlate_ExplicitUnavailableCoverage_ReturnsUnavailable()
    {
        var evidence = Loaded(AnonymousVisualEvidenceInterval.Coverage(
            CoverageId,
            "session",
            TimeSpan.Zero,
            TimeSpan.FromSeconds(10),
            AnonymousVisualAnalysisAvailability.Unavailable,
            1,
            Provenance));

        var result = Correlator().Correlate(Segment(AudioSourceKind.SystemOutput), evidence);

        Assert.Equal(AnonymousVisualCorrelationOutcome.Unavailable, result.Outcome);
        Assert.Equal(AnonymousVisualCorrelationReason.NoAvailableCoverage, result.Reason);
    }

    [Fact]
    public void Correlate_UnavailableLowConfidenceCoverage_ReturnsUnavailableBeforeConfidenceCheck()
    {
        var evidence = Loaded(AnonymousVisualEvidenceInterval.Coverage(
            CoverageId,
            "session",
            TimeSpan.Zero,
            TimeSpan.FromSeconds(10),
            AnonymousVisualAnalysisAvailability.Unavailable,
            0,
            Provenance));

        var result = Correlator().Correlate(Segment(AudioSourceKind.SystemOutput), evidence);

        Assert.Equal(AnonymousVisualCorrelationOutcome.Unavailable, result.Outcome);
        Assert.Equal(AnonymousVisualCorrelationReason.NoAvailableCoverage, result.Reason);
    }

    [Fact]
    public void Correlate_PartialCoverage_AbstainsExplicitly()
    {
        var evidence = Loaded(Coverage(TimeSpan.Zero, TimeSpan.FromSeconds(4)));

        var result = Correlator().Correlate(Segment(AudioSourceKind.SystemOutput), evidence);

        Assert.Equal(AnonymousVisualCorrelationOutcome.Abstained, result.Outcome);
        Assert.Equal(AnonymousVisualCorrelationReason.InsufficientCoverage, result.Reason);
        Assert.Equal(0.4, result.CoverageRatio, 6);
    }

    [Fact]
    public void Correlate_LowConfidenceEvidence_AbstainsExplicitly()
    {
        var evidence = Loaded(AnonymousVisualEvidenceInterval.Coverage(
            CoverageId,
            "session",
            TimeSpan.Zero,
            TimeSpan.FromSeconds(10),
            AnonymousVisualAnalysisAvailability.Available,
            0.4,
            Provenance));

        var result = Correlator().Correlate(Segment(AudioSourceKind.SystemOutput), evidence);

        Assert.Equal(AnonymousVisualCorrelationOutcome.Abstained, result.Outcome);
        Assert.Equal(AnonymousVisualCorrelationReason.LowConfidence, result.Reason);
    }

    [Fact]
    public void Correlate_DifferentSession_AbstainsExplicitly()
    {
        var result = Correlator().Correlate(
            Segment(AudioSourceKind.SystemOutput),
            AnonymousVisualEvidenceReadResult.Loaded("another-session", []));

        Assert.Equal(AnonymousVisualCorrelationOutcome.Abstained, result.Outcome);
        Assert.Equal(AnonymousVisualCorrelationReason.SessionMismatch, result.Reason);
    }

    [Fact]
    public void Correlate_UnknownEvidenceVersion_AbstainsExplicitly()
    {
        var unknownProvenance = Provenance with { DetectorVersion = 99 };
        var evidence = Loaded(AnonymousVisualEvidenceInterval.Coverage(
            CoverageId,
            "session",
            TimeSpan.Zero,
            TimeSpan.FromSeconds(10),
            AnonymousVisualAnalysisAvailability.Available,
            1,
            unknownProvenance));

        var result = Correlator().Correlate(Segment(AudioSourceKind.SystemOutput), evidence);

        Assert.Equal(AnonymousVisualCorrelationOutcome.Abstained, result.Outcome);
        Assert.Equal(AnonymousVisualCorrelationReason.UnsupportedEvidenceVersion, result.Reason);
    }

    [Fact]
    public void Correlate_UnknownProfileVersion_AbstainsExplicitly()
    {
        var unknownProvenance = Provenance with { ProfileVersion = 99 };
        var evidence = Loaded(AnonymousVisualEvidenceInterval.Coverage(
            CoverageId,
            "session",
            TimeSpan.Zero,
            TimeSpan.FromSeconds(10),
            AnonymousVisualAnalysisAvailability.Available,
            1,
            unknownProvenance));

        var result = Correlator().Correlate(Segment(AudioSourceKind.SystemOutput), evidence);

        Assert.Equal(AnonymousVisualCorrelationOutcome.Abstained, result.Outcome);
        Assert.Equal(AnonymousVisualCorrelationReason.UnsupportedEvidenceVersion, result.Reason);
    }

    [Fact]
    public void Correlate_ProviderUnsupportedByPolicy_AbstainsExplicitly()
    {
        var otherProvider = Provenance with { Provider = MeetingProvider.MicrosoftTeams };
        var evidence = Loaded(AnonymousVisualEvidenceInterval.Coverage(
            CoverageId,
            "session",
            TimeSpan.Zero,
            TimeSpan.FromSeconds(10),
            AnonymousVisualAnalysisAvailability.Available,
            1,
            otherProvider));

        var result = Correlator().Correlate(Segment(AudioSourceKind.SystemOutput), evidence);

        Assert.Equal(AnonymousVisualCorrelationOutcome.Abstained, result.Outcome);
        Assert.Equal(AnonymousVisualCorrelationReason.UnsupportedProvider, result.Reason);
    }

    [Fact]
    public void Correlate_MixedProviderProvenance_AbstainsExplicitly()
    {
        var evidence = Loaded(
            Coverage(TimeSpan.Zero, TimeSpan.FromSeconds(10)),
            AnonymousVisualEvidenceInterval.Activity(
                ActivityId,
                "session",
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(8),
                1,
                Provenance with { Provider = MeetingProvider.MicrosoftTeams }));

        var result = Correlator().Correlate(Segment(AudioSourceKind.SystemOutput), evidence);

        Assert.Equal(AnonymousVisualCorrelationOutcome.Abstained, result.Outcome);
        Assert.Equal(AnonymousVisualCorrelationReason.MixedProvenance, result.Reason);
    }

    [Fact]
    public void Correlate_OverlappingAvailableAndUnavailableCoverage_AbstainsExplicitly()
    {
        var evidence = Loaded(
            Coverage(TimeSpan.Zero, TimeSpan.FromSeconds(10)),
            AnonymousVisualEvidenceInterval.Coverage(
                Guid.ParseExact("33333333333333333333333333333333", "N"),
                "session",
                TimeSpan.FromSeconds(4),
                TimeSpan.FromSeconds(6),
                AnonymousVisualAnalysisAvailability.Unavailable,
                1,
                Provenance));

        var result = Correlator().Correlate(Segment(AudioSourceKind.SystemOutput), evidence);

        Assert.Equal(AnonymousVisualCorrelationOutcome.Abstained, result.Outcome);
        Assert.Equal(AnonymousVisualCorrelationReason.ContradictoryAvailability, result.Reason);
    }

    [Fact]
    public void Correlate_ActivityOutsideAvailableCoverage_AbstainsInsteadOfMatching()
    {
        var evidence = Loaded(
            Coverage(TimeSpan.Zero, TimeSpan.FromSeconds(9)),
            Activity(TimeSpan.Zero, TimeSpan.FromSeconds(10)));

        var result = Correlator().Correlate(Segment(AudioSourceKind.SystemOutput), evidence);

        Assert.Equal(AnonymousVisualCorrelationOutcome.Abstained, result.Outcome);
        Assert.Equal(AnonymousVisualCorrelationReason.ActivityOutsideCoverage, result.Reason);
        Assert.False(result.HasEvidence);
    }

    [Fact]
    public void Correlate_OverlappingActivityIntervals_CountOnlyTheirCoveredUnion()
    {
        var evidence = Loaded(
            Coverage(TimeSpan.Zero, TimeSpan.FromSeconds(10)),
            Activity(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4)),
            AnonymousVisualEvidenceInterval.Activity(
                Guid.ParseExact("44444444444444444444444444444444", "N"),
                "session",
                TimeSpan.FromSeconds(3),
                TimeSpan.FromSeconds(6),
                1,
                Provenance));

        var result = Correlator().Correlate(Segment(AudioSourceKind.SystemOutput), evidence);

        Assert.Equal(AnonymousVisualCorrelationOutcome.Matched, result.Outcome);
        Assert.Equal(0.5, result.ActivityOverlapRatio, 6);
    }

    [Theory]
    [InlineData(AnonymousVisualEvidenceReadStatus.UnsupportedVersion, AnonymousVisualCorrelationReason.UnsupportedPayloadVersion)]
    [InlineData(AnonymousVisualEvidenceReadStatus.Corrupted, AnonymousVisualCorrelationReason.CorruptedEvidence)]
    public void Correlate_UnreadableEvidence_AbstainsSafely(
        AnonymousVisualEvidenceReadStatus status,
        AnonymousVisualCorrelationReason expectedReason)
    {
        var evidence = status == AnonymousVisualEvidenceReadStatus.UnsupportedVersion
            ? AnonymousVisualEvidenceReadResult.UnsupportedVersion("session")
            : AnonymousVisualEvidenceReadResult.Corrupted("session");

        var result = Correlator().Correlate(Segment(AudioSourceKind.SystemOutput), evidence);

        Assert.Equal(AnonymousVisualCorrelationOutcome.Abstained, result.Outcome);
        Assert.Equal(expectedReason, result.Reason);
    }

    [Fact]
    public void Correlate_DoesNotMutateTranscriptOrAddEvidenceToExistingExports()
    {
        var segment = Segment(AudioSourceKind.SystemOutput);
        var reviewed = new ReviewedTranscriptSegment(segment, null);
        var session = new SessionSummary("session", "Meeting", DateTimeOffset.UnixEpoch, null, SessionState.Completed);
        var plainTextBefore = TranscriptExport.CreatePlainText([reviewed]);
        var markdownBefore = ObsidianMarkdownExport.Create(session, [reviewed], "test");

        var result = Correlator().Correlate(
            segment,
            Loaded(
                Coverage(TimeSpan.Zero, TimeSpan.FromSeconds(10)),
                Activity(TimeSpan.Zero, TimeSpan.FromSeconds(10))));

        Assert.True(result.HasEvidence);
        Assert.Same(segment, reviewed.Segment);
        Assert.Equal(plainTextBefore, TranscriptExport.CreatePlainText([reviewed]));
        Assert.Equal(markdownBefore, ObsidianMarkdownExport.Create(session, [reviewed], "test"));
    }

    private static AnonymousVisualActivityCorrelator Correlator() => new(Policy());

    private static AnonymousVisualCorrelationPolicy Policy() => new(
        Provider: MeetingProvider.GoogleMeet,
        ProfileVersion: 1,
        EvidenceVersion: 1,
        DetectorVersion: 1,
        PolicyVersion: 1,
        MinimumConfidence: 0.75,
        MinimumCoverageRatio: 0.8,
        MinimumActivityOverlapRatio: 0.25);

    private static TranscriptSegment Segment(AudioSourceKind source) => new(
        "segment",
        "session",
        source,
        1,
        TimeSpan.Zero,
        TimeSpan.FromSeconds(10),
        "transcript",
        DateTimeOffset.UnixEpoch);

    private static AnonymousVisualEvidenceReadResult Loaded(params AnonymousVisualEvidenceInterval[] intervals) =>
        AnonymousVisualEvidenceReadResult.Loaded("session", intervals);

    private static AnonymousVisualEvidenceInterval Coverage(TimeSpan start, TimeSpan end) =>
        AnonymousVisualEvidenceInterval.Coverage(
            CoverageId,
            "session",
            start,
            end,
            AnonymousVisualAnalysisAvailability.Available,
            1,
            Provenance);

    private static AnonymousVisualEvidenceInterval Activity(TimeSpan start, TimeSpan end) =>
        AnonymousVisualEvidenceInterval.Activity(ActivityId, "session", start, end, 1, Provenance);
}
