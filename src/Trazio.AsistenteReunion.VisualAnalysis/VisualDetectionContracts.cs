using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.App;

internal enum VisualProbeProfileValidationState
{
    Unsupported = 0,
    Unvalidated = 1,
    Validated = 2
}

internal interface IVisualProbeProfile
{
    MeetingProvider Provider { get; }
    int Version { get; }
    VisualProbeProfileValidationState ValidationState { get; }
    int ExpectedFeatureCount { get; }
    VisualProbeDetectionPolicy? DetectionPolicy { get; }
}

internal sealed record VisualProbeDetectionPolicy
{
    public const int MinimumScore = 0;
    public const int MaximumScore = 1_000;

    public VisualProbeDetectionPolicy(
        int enterThreshold,
        int exitThreshold,
        TimeSpan activationHold,
        TimeSpan releaseHold,
        TimeSpan maxObservationGap,
        int minimumCoherentPatches,
        int evidenceVersion,
        int detectorVersion,
        int policyVersion)
    {
        if (enterThreshold is < MinimumScore or > MaximumScore)
            throw new ArgumentOutOfRangeException(nameof(enterThreshold));
        if (exitThreshold is < MinimumScore or > MaximumScore || exitThreshold >= enterThreshold)
            throw new ArgumentOutOfRangeException(nameof(exitThreshold));
        if (activationHold < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(activationHold));
        if (releaseHold < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(releaseHold));
        if (maxObservationGap <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maxObservationGap));
        if (minimumCoherentPatches <= 0) throw new ArgumentOutOfRangeException(nameof(minimumCoherentPatches));
        if (evidenceVersion <= 0) throw new ArgumentOutOfRangeException(nameof(evidenceVersion));
        if (detectorVersion <= 0) throw new ArgumentOutOfRangeException(nameof(detectorVersion));
        if (policyVersion <= 0) throw new ArgumentOutOfRangeException(nameof(policyVersion));

        EnterThreshold = enterThreshold;
        ExitThreshold = exitThreshold;
        ActivationHold = activationHold;
        ReleaseHold = releaseHold;
        MaxObservationGap = maxObservationGap;
        MinimumCoherentPatches = minimumCoherentPatches;
        EvidenceVersion = evidenceVersion;
        DetectorVersion = detectorVersion;
        PolicyVersion = policyVersion;
    }

    public int EnterThreshold { get; }
    public int ExitThreshold { get; }
    public TimeSpan ActivationHold { get; }
    public TimeSpan ReleaseHold { get; }
    public TimeSpan MaxObservationGap { get; }
    public int MinimumCoherentPatches { get; }
    public int EvidenceVersion { get; }
    public int DetectorVersion { get; }
    public int PolicyVersion { get; }
}

internal readonly record struct VisualProbeFeature
{
    public VisualProbeFeature(int activityScore)
        : this(
            activityScore,
            activityScore / (double)VisualProbeDetectionPolicy.MaximumScore,
            activityScore == 0 ? 0d : 1d,
            activityScore / (double)VisualProbeDetectionPolicy.MaximumScore)
    {
    }

    public VisualProbeFeature(
        double highlightMatchRatio,
        double nonBlackRatio,
        double meanLuma)
        : this(
            checked((int)Math.Round(
                ValidateRatio(highlightMatchRatio, nameof(highlightMatchRatio)) *
                VisualProbeDetectionPolicy.MaximumScore,
                MidpointRounding.AwayFromZero)),
            highlightMatchRatio,
            ValidateRatio(nonBlackRatio, nameof(nonBlackRatio)),
            ValidateRatio(meanLuma, nameof(meanLuma)))
    {
    }

    private VisualProbeFeature(
        int activityScore,
        double highlightMatchRatio,
        double nonBlackRatio,
        double meanLuma)
    {
        if (activityScore is < VisualProbeDetectionPolicy.MinimumScore or > VisualProbeDetectionPolicy.MaximumScore)
            throw new ArgumentOutOfRangeException(nameof(activityScore));
        ActivityScore = activityScore;
        HighlightMatchRatio = ValidateRatio(highlightMatchRatio, nameof(highlightMatchRatio));
        NonBlackRatio = ValidateRatio(nonBlackRatio, nameof(nonBlackRatio));
        MeanLuma = ValidateRatio(meanLuma, nameof(meanLuma));
    }

    public int ActivityScore { get; }
    public double HighlightMatchRatio { get; }
    public double NonBlackRatio { get; }
    public double MeanLuma { get; }

    private static double ValidateRatio(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value is < 0d or > 1d)
            throw new ArgumentOutOfRangeException(parameterName);
        return value;
    }
}

internal enum VisualProbeObservationStatus
{
    Available = 0,
    Unavailable = 1,
    Completed = 2
}

internal readonly record struct VisualProbeObservation
{
    private VisualProbeObservation(
        VisualProbeObservationStatus status,
        TimeSpan offset,
        long surfaceRevision,
        IVisualProbeProfile? profile)
    {
        if (offset < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(offset));
        if (status != VisualProbeObservationStatus.Completed)
        {
            ArgumentNullException.ThrowIfNull(profile);
            if (surfaceRevision < 0) throw new ArgumentOutOfRangeException(nameof(surfaceRevision));
        }

        Status = status;
        Offset = offset;
        SurfaceRevision = surfaceRevision;
        Profile = profile;
    }

    public VisualProbeObservationStatus Status { get; }
    public TimeSpan Offset { get; }
    public long SurfaceRevision { get; }
    public IVisualProbeProfile? Profile { get; }

    public static VisualProbeObservation Available(
        TimeSpan offset,
        long surfaceRevision,
        IVisualProbeProfile profile) =>
        new(VisualProbeObservationStatus.Available, offset, surfaceRevision, profile);

    public static VisualProbeObservation Unavailable(
        TimeSpan offset,
        long surfaceRevision,
        IVisualProbeProfile profile) =>
        new(VisualProbeObservationStatus.Unavailable, offset, surfaceRevision, profile);

    public static VisualProbeObservation Completed(TimeSpan offset) =>
        new(VisualProbeObservationStatus.Completed, offset, 0, null);
}

internal enum VisualProbeDetectionDisposition
{
    Observed = 0,
    Abstained = 1,
    Completed = 2
}

internal enum VisualProbeAbstentionReason
{
    None = 0,
    UnsupportedProfile = 1,
    UnvalidatedProfile = 2,
    SourceUnavailable = 3,
    InsufficientCoherence = 4,
    AmbiguousFeatures = 5,
    ObservationGap = 6,
    ProfileChanged = 7,
    SurfaceChanged = 8
}

internal sealed class VisualProbeDetectionResult
{
    private VisualProbeDetectionResult(
        VisualProbeDetectionDisposition disposition,
        VisualProbeAbstentionReason reason,
        IEnumerable<AnonymousVisualEvidenceInterval> intervals)
    {
        Disposition = disposition;
        Reason = reason;
        Intervals = Array.AsReadOnly(intervals.ToArray());
    }

    public VisualProbeDetectionDisposition Disposition { get; }
    public VisualProbeAbstentionReason Reason { get; }
    public IReadOnlyList<AnonymousVisualEvidenceInterval> Intervals { get; }

    public static VisualProbeDetectionResult Observed(
        IEnumerable<AnonymousVisualEvidenceInterval> intervals) =>
        new(VisualProbeDetectionDisposition.Observed, VisualProbeAbstentionReason.None, intervals);

    public static VisualProbeDetectionResult Abstained(
        VisualProbeAbstentionReason reason,
        IEnumerable<AnonymousVisualEvidenceInterval> intervals) =>
        reason == VisualProbeAbstentionReason.None
            ? throw new ArgumentOutOfRangeException(nameof(reason))
            : new(VisualProbeDetectionDisposition.Abstained, reason, intervals);

    public static VisualProbeDetectionResult Completed(
        IEnumerable<AnonymousVisualEvidenceInterval> intervals) =>
        new(VisualProbeDetectionDisposition.Completed, VisualProbeAbstentionReason.None, intervals);
}

internal interface IVisualProbeDetector
{
    VisualProbeDetectionResult Observe(
        VisualProbeObservation observation,
        ReadOnlySpan<VisualProbeFeature> features);
}
