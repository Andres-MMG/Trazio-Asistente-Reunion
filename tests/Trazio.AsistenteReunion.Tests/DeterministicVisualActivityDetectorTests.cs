using Trazio.AsistenteReunion.App;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.Tests;

public sealed class DeterministicVisualActivityDetectorTests
{
    private const string SessionId = "session-visual-detector";

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void Observe_WithProductionUnvalidatedProfile_AbstainsAndClosesUnavailableCoverage()
    {
        var profile = VisualProbeProfiles.GetProduction(MeetingProvider.GoogleMeet);
        var detector = new DeterministicVisualActivityDetector(SessionId);

        var observed = detector.Observe(
            VisualProbeObservation.Available(TimeSpan.Zero, 1, profile),
            []);
        var completed = detector.Observe(
            VisualProbeObservation.Completed(TimeSpan.FromSeconds(1)),
            []);

        Assert.Equal(VisualProbeDetectionDisposition.Abstained, observed.Disposition);
        Assert.Equal(VisualProbeAbstentionReason.UnvalidatedProfile, observed.Reason);
        var coverage = Assert.Single(completed.Intervals);
        Assert.Equal(AnonymousVisualEvidenceKind.Coverage, coverage.Kind);
        Assert.Equal(AnonymousVisualAnalysisAvailability.Unavailable, coverage.Availability);
        Assert.Equal(TimeSpan.Zero, coverage.Start);
        Assert.Equal(TimeSpan.FromSeconds(1), coverage.End);
        Assert.Equal(AnonymousVisualEvidenceVersions.CurrentProfile, coverage.Provenance.ProfileVersion);
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void Observe_WithEnterAndExitHolds_AppliesHysteresisAndClosesActivityAndCoverage()
    {
        var profile = Profile(version: 7, activationSeconds: 2, releaseSeconds: 2);
        var detector = new DeterministicVisualActivityDetector(SessionId);
        var intervals = new List<AnonymousVisualEvidenceInterval>();

        intervals.AddRange(Observe(detector, profile, 0, Scores(900, 900, 900)).Intervals);
        intervals.AddRange(Observe(detector, profile, 1, Scores(850, 900, 950)).Intervals);
        intervals.AddRange(Observe(detector, profile, 2, Scores(900, 900, 900)).Intervals);
        intervals.AddRange(Observe(detector, profile, 3, Scores(500, 500, 500)).Intervals);
        intervals.AddRange(Observe(detector, profile, 4, Scores(100, 100, 100)).Intervals);
        intervals.AddRange(Observe(detector, profile, 5, Scores(100, 100, 100)).Intervals);
        intervals.AddRange(Observe(detector, profile, 6, Scores(100, 100, 100)).Intervals);
        intervals.AddRange(detector.Observe(
            VisualProbeObservation.Completed(TimeSpan.FromSeconds(7)), []).Intervals);

        var activity = Assert.Single(intervals, interval =>
            interval.Kind == AnonymousVisualEvidenceKind.Activity);
        var coverage = Assert.Single(intervals, interval =>
            interval.Kind == AnonymousVisualEvidenceKind.Coverage);
        Assert.Equal(TimeSpan.Zero, activity.Start);
        Assert.Equal(TimeSpan.FromSeconds(4), activity.End);
        Assert.Equal(AnonymousVisualAnalysisAvailability.Available, coverage.Availability);
        Assert.Equal(TimeSpan.Zero, coverage.Start);
        Assert.Equal(TimeSpan.FromSeconds(7), coverage.End);
        Assert.Equal(7, activity.Provenance.ProfileVersion);
        Assert.Equal(3, activity.Provenance.EvidenceVersion);
        Assert.Equal(4, activity.Provenance.DetectorVersion);
        Assert.Equal(5, activity.Provenance.PolicyVersion);
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void Observe_WhenBandInterruptsActivation_RequiresANewContinuousHold()
    {
        var profile = Profile(version: 7, activationSeconds: 2, releaseSeconds: 1);
        var detector = new DeterministicVisualActivityDetector(SessionId);
        var intervals = new List<AnonymousVisualEvidenceInterval>();

        Observe(detector, profile, 0, Scores(900, 900, 900));
        Observe(detector, profile, 1, Scores(500, 500, 500));
        Observe(detector, profile, 2, Scores(900, 900, 900));
        Observe(detector, profile, 3, Scores(900, 900, 900));
        Observe(detector, profile, 4, Scores(900, 900, 900));
        intervals.AddRange(detector.Observe(
            VisualProbeObservation.Completed(TimeSpan.FromSeconds(5)), []).Intervals);

        var activity = Assert.Single(intervals, interval =>
            interval.Kind == AnonymousVisualEvidenceKind.Activity);
        Assert.Equal(TimeSpan.FromSeconds(2), activity.Start);
        Assert.Equal(TimeSpan.FromSeconds(5), activity.End);
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void Observe_WhenObservationGapExceedsMaximum_ClosesAtLastObservationAndMarksGapUnavailable()
    {
        var profile = Profile(version: 7, activationSeconds: 0, releaseSeconds: 1, maxGapSeconds: 1);
        var detector = new DeterministicVisualActivityDetector(SessionId);
        Observe(detector, profile, 0, Scores(900, 900, 900));
        Observe(detector, profile, 1, Scores(900, 900, 900));

        var gap = Observe(detector, profile, 3, Scores(900, 900, 900));
        var completed = detector.Observe(
            VisualProbeObservation.Completed(TimeSpan.FromSeconds(4)), []);
        var intervals = gap.Intervals.Concat(completed.Intervals).ToArray();

        Assert.Equal(VisualProbeDetectionDisposition.Abstained, gap.Disposition);
        Assert.Equal(VisualProbeAbstentionReason.ObservationGap, gap.Reason);
        Assert.Contains(intervals, interval =>
            interval.Kind == AnonymousVisualEvidenceKind.Activity &&
            interval.Start == TimeSpan.Zero && interval.End == TimeSpan.FromSeconds(1));
        Assert.Contains(intervals, interval =>
            interval.Kind == AnonymousVisualEvidenceKind.Coverage &&
            interval.Availability == AnonymousVisualAnalysisAvailability.Available &&
            interval.Start == TimeSpan.Zero && interval.End == TimeSpan.FromSeconds(1));
        Assert.Contains(intervals, interval =>
            interval.Kind == AnonymousVisualEvidenceKind.Coverage &&
            interval.Availability == AnonymousVisualAnalysisAvailability.Unavailable &&
            interval.Start == TimeSpan.FromSeconds(1) && interval.End == TimeSpan.FromSeconds(3));
        Assert.Contains(intervals, interval =>
            interval.Kind == AnonymousVisualEvidenceKind.Activity &&
            interval.Start == TimeSpan.FromSeconds(3) && interval.End == TimeSpan.FromSeconds(4));
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void Observe_WithContradictoryCoherentFeatures_AbstainsAndResetsFailClosed()
    {
        var profile = Profile(version: 7, expectedFeatureCount: 4, minimumCoherentPatches: 2,
            activationSeconds: 0, releaseSeconds: 1);
        var detector = new DeterministicVisualActivityDetector(SessionId);
        Observe(detector, profile, 0, Scores(900, 900, 900, 900));

        var ambiguous = Observe(detector, profile, 1, Scores(900, 900, 100, 100));
        var completed = detector.Observe(
            VisualProbeObservation.Completed(TimeSpan.FromSeconds(2)), []);
        var intervals = ambiguous.Intervals.Concat(completed.Intervals).ToArray();

        Assert.Equal(VisualProbeDetectionDisposition.Abstained, ambiguous.Disposition);
        Assert.Equal(VisualProbeAbstentionReason.AmbiguousFeatures, ambiguous.Reason);
        Assert.Contains(intervals, interval =>
            interval.Kind == AnonymousVisualEvidenceKind.Activity && interval.End == TimeSpan.FromSeconds(1));
        Assert.Contains(intervals, interval =>
            interval.Kind == AnonymousVisualEvidenceKind.Coverage &&
            interval.Availability == AnonymousVisualAnalysisAvailability.Unavailable &&
            interval.Start == TimeSpan.FromSeconds(1) && interval.End == TimeSpan.FromSeconds(2));
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void Observe_WithTooFewCoherentFeatures_AbstainsInsteadOfInferringActivity()
    {
        var profile = Profile(version: 7, expectedFeatureCount: 3, minimumCoherentPatches: 2);
        var detector = new DeterministicVisualActivityDetector(SessionId);

        var result = Observe(detector, profile, 0, Scores(900, 500, 500));

        Assert.Equal(VisualProbeDetectionDisposition.Abstained, result.Disposition);
        Assert.Equal(VisualProbeAbstentionReason.InsufficientCoherence, result.Reason);
        Assert.Empty(result.Intervals);
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void Observe_WhenSourceBecomesUnavailable_ClosesActivityAndCompletesUnavailableCoverage()
    {
        var profile = Profile(version: 7, activationSeconds: 0, releaseSeconds: 1);
        var detector = new DeterministicVisualActivityDetector(SessionId);
        Observe(detector, profile, 0, Scores(900, 900, 900));

        var unavailable = detector.Observe(
            VisualProbeObservation.Unavailable(TimeSpan.FromSeconds(1), 1, profile), []);
        var completed = detector.Observe(
            VisualProbeObservation.Completed(TimeSpan.FromSeconds(2)), []);
        var intervals = unavailable.Intervals.Concat(completed.Intervals).ToArray();

        Assert.Equal(VisualProbeAbstentionReason.SourceUnavailable, unavailable.Reason);
        Assert.Contains(intervals, interval =>
            interval.Kind == AnonymousVisualEvidenceKind.Activity &&
            interval.Start == TimeSpan.Zero && interval.End == TimeSpan.FromSeconds(1));
        Assert.Contains(intervals, interval =>
            interval.Kind == AnonymousVisualEvidenceKind.Coverage &&
            interval.Availability == AnonymousVisualAnalysisAvailability.Available &&
            interval.Start == TimeSpan.Zero && interval.End == TimeSpan.FromSeconds(1));
        Assert.Contains(intervals, interval =>
            interval.Kind == AnonymousVisualEvidenceKind.Coverage &&
            interval.Availability == AnonymousVisualAnalysisAvailability.Unavailable &&
            interval.Start == TimeSpan.FromSeconds(1) && interval.End == TimeSpan.FromSeconds(2));
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void Observe_WhenProfileVersionChanges_ResetsAcrossUnavailableBoundaryWithCompleteProvenance()
    {
        var original = Profile(version: 7, activationSeconds: 0, releaseSeconds: 1);
        var revised = Profile(version: 8, activationSeconds: 0, releaseSeconds: 1);
        var detector = new DeterministicVisualActivityDetector(SessionId);
        Observe(detector, original, 0, Scores(900, 900, 900));
        Observe(detector, original, 1, Scores(900, 900, 900));

        var changed = Observe(detector, revised, 2, Scores(900, 900, 900));
        var completed = detector.Observe(
            VisualProbeObservation.Completed(TimeSpan.FromSeconds(3)), []);
        var intervals = changed.Intervals.Concat(completed.Intervals).ToArray();

        Assert.Equal(VisualProbeAbstentionReason.ProfileChanged, changed.Reason);
        Assert.Contains(intervals, interval =>
            interval.Kind == AnonymousVisualEvidenceKind.Coverage &&
            interval.Availability == AnonymousVisualAnalysisAvailability.Unavailable &&
            interval.Start == TimeSpan.FromSeconds(1) && interval.End == TimeSpan.FromSeconds(2) &&
            interval.Provenance.ProfileVersion == 7);
        Assert.Contains(intervals, interval =>
            interval.Kind == AnonymousVisualEvidenceKind.Activity &&
            interval.Start == TimeSpan.FromSeconds(2) && interval.End == TimeSpan.FromSeconds(3) &&
            interval.Provenance.ProfileVersion == 8);
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void Observe_WhenSurfaceRevisionChanges_ResetsAcrossUnavailableBoundary()
    {
        var profile = Profile(version: 7, activationSeconds: 0, releaseSeconds: 1);
        var detector = new DeterministicVisualActivityDetector(SessionId);
        Observe(detector, profile, 0, Scores(900, 900, 900), surfaceRevision: 1);
        Observe(detector, profile, 1, Scores(900, 900, 900), surfaceRevision: 1);

        var changed = Observe(detector, profile, 2, Scores(900, 900, 900), surfaceRevision: 2);

        Assert.Equal(VisualProbeDetectionDisposition.Abstained, changed.Disposition);
        Assert.Equal(VisualProbeAbstentionReason.SurfaceChanged, changed.Reason);
        Assert.Contains(changed.Intervals, interval =>
            interval.Kind == AnonymousVisualEvidenceKind.Coverage &&
            interval.Availability == AnonymousVisualAnalysisAvailability.Unavailable &&
            interval.Start == TimeSpan.FromSeconds(1) && interval.End == TimeSpan.FromSeconds(2));
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void Observe_WithSameSequence_ProducesIdenticalIntervalsAndOpaqueIds()
    {
        var profile = Profile(version: 7, activationSeconds: 0, releaseSeconds: 1);

        var first = DetectOneSecondActivity(profile);
        var second = DetectOneSecondActivity(profile);

        Assert.Equal(first, second);
        Assert.All(first, interval => Assert.NotEqual(Guid.Empty, interval.Id));
    }

    private static VisualProbeDetectionResult Observe(
        DeterministicVisualActivityDetector detector,
        IVisualProbeProfile profile,
        int seconds,
        VisualProbeFeature[] features,
        long surfaceRevision = 1) =>
        detector.Observe(
            VisualProbeObservation.Available(TimeSpan.FromSeconds(seconds), surfaceRevision, profile),
            features);

    private static VisualProbeFeature[] Scores(params int[] values) =>
        values.Select(value => new VisualProbeFeature(value)).ToArray();

    private static AnonymousVisualEvidenceInterval[] DetectOneSecondActivity(IVisualProbeProfile profile)
    {
        var detector = new DeterministicVisualActivityDetector(SessionId);
        detector.Observe(
            VisualProbeObservation.Available(TimeSpan.Zero, 1, profile),
            Scores(900, 900, 900));
        return detector.Observe(
            VisualProbeObservation.Completed(TimeSpan.FromSeconds(1)), []).Intervals.ToArray();
    }

    private static SyntheticValidatedProfile Profile(
        int version,
        int expectedFeatureCount = 3,
        int minimumCoherentPatches = 2,
        int activationSeconds = 1,
        int releaseSeconds = 1,
        int maxGapSeconds = 2) =>
        new(
            version,
            expectedFeatureCount,
            new VisualProbeDetectionPolicy(
                enterThreshold: 800,
                exitThreshold: 200,
                activationHold: TimeSpan.FromSeconds(activationSeconds),
                releaseHold: TimeSpan.FromSeconds(releaseSeconds),
                maxObservationGap: TimeSpan.FromSeconds(maxGapSeconds),
                minimumCoherentPatches: minimumCoherentPatches,
                evidenceVersion: 3,
                detectorVersion: 4,
                policyVersion: 5));

    private sealed record SyntheticValidatedProfile(
        int Version,
        int ExpectedFeatureCount,
        VisualProbeDetectionPolicy DetectionPolicy) : IVisualProbeProfile
    {
        public MeetingProvider Provider => MeetingProvider.GoogleMeet;
        public VisualProbeProfileValidationState ValidationState => VisualProbeProfileValidationState.Validated;
        VisualProbeDetectionPolicy? IVisualProbeProfile.DetectionPolicy => DetectionPolicy;
    }
}
