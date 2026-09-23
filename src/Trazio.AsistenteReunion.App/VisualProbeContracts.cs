using System.Buffers;
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

internal readonly record struct VisualProbePatch
{
    public const int Width = 16;
    public const int Height = 16;

    public VisualProbePatch(
        int left,
        int top,
        byte highlightBlue,
        byte highlightGreen,
        byte highlightRed,
        byte highlightTolerance)
    {
        if (left < 0) throw new ArgumentOutOfRangeException(nameof(left));
        if (top < 0) throw new ArgumentOutOfRangeException(nameof(top));

        Left = left;
        Top = top;
        HighlightBlue = highlightBlue;
        HighlightGreen = highlightGreen;
        HighlightRed = highlightRed;
        HighlightTolerance = highlightTolerance;
    }

    public int Left { get; }
    public int Top { get; }
    public byte HighlightBlue { get; }
    public byte HighlightGreen { get; }
    public byte HighlightRed { get; }
    public byte HighlightTolerance { get; }
}

internal sealed class VisualProbeLayoutProfile : IVisualProbeProfile
{
    private readonly IReadOnlyList<VisualProbePatch> _patches;

    public VisualProbeLayoutProfile(
        MeetingProvider provider,
        int version,
        VisualProbeProfileValidationState validationState,
        int expectedSurfaceWidth,
        int expectedSurfaceHeight,
        IEnumerable<VisualProbePatch> patches,
        VisualProbeDetectionPolicy? detectionPolicy)
    {
        if (!Enum.IsDefined(provider)) throw new ArgumentOutOfRangeException(nameof(provider));
        if (version <= 0) throw new ArgumentOutOfRangeException(nameof(version));
        if (!Enum.IsDefined(validationState)) throw new ArgumentOutOfRangeException(nameof(validationState));
        if (expectedSurfaceWidth <= 0) throw new ArgumentOutOfRangeException(nameof(expectedSurfaceWidth));
        if (expectedSurfaceHeight <= 0) throw new ArgumentOutOfRangeException(nameof(expectedSurfaceHeight));
        ArgumentNullException.ThrowIfNull(patches);

        var patchArray = patches.ToArray();
        if (patchArray.Length > D3D11VisualProbeExtractor.MaxPatches)
            throw new ArgumentOutOfRangeException(nameof(patches));
        if (validationState == VisualProbeProfileValidationState.Validated)
        {
            if (patchArray.Length == 0) throw new ArgumentException("A validated profile requires patches.", nameof(patches));
            ArgumentNullException.ThrowIfNull(detectionPolicy);
            if (detectionPolicy.MinimumCoherentPatches > patchArray.Length)
                throw new ArgumentException("The detection policy requires more patches than the layout provides.", nameof(detectionPolicy));
        }
        else if (detectionPolicy is not null)
        {
            throw new ArgumentException("Only validated profiles can contain a detection policy.", nameof(detectionPolicy));
        }

        Provider = provider;
        Version = version;
        ValidationState = validationState;
        ExpectedSurfaceWidth = expectedSurfaceWidth;
        ExpectedSurfaceHeight = expectedSurfaceHeight;
        _patches = Array.AsReadOnly(patchArray);
        DetectionPolicy = detectionPolicy;
    }

    public MeetingProvider Provider { get; }
    public int Version { get; }
    public VisualProbeProfileValidationState ValidationState { get; }
    public int ExpectedSurfaceWidth { get; }
    public int ExpectedSurfaceHeight { get; }
    public IReadOnlyList<VisualProbePatch> Patches => _patches;
    public int ExpectedFeatureCount => _patches.Count;
    public VisualProbeDetectionPolicy? DetectionPolicy { get; }
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

internal sealed class VisualProbeFeatureLease : IDisposable
{
    private readonly ArrayPool<VisualProbeFeature> _pool;
    private VisualProbeFeature[]? _features;
    private readonly int _featureCount;
    private int _disposed;

    private VisualProbeFeatureLease(
        VisualProbeObservation observation,
        ReadOnlySpan<VisualProbeFeature> features,
        ArrayPool<VisualProbeFeature> pool)
    {
        if (!Enum.IsDefined(observation.Status))
            throw new ArgumentOutOfRangeException(nameof(observation));
        if (observation.Status != VisualProbeObservationStatus.Completed && observation.Profile is null)
            throw new ArgumentException("A profile is required for probe observations.", nameof(observation));
        if (observation.Status != VisualProbeObservationStatus.Available && !features.IsEmpty)
            throw new ArgumentException("Only available observations can contain aggregate features.", nameof(features));

        Observation = observation;
        _pool = pool;
        _featureCount = features.Length;
        if (features.IsEmpty) return;
        _features = pool.Rent(features.Length);
        features.CopyTo(_features);
    }

    public VisualProbeObservation Observation { get; }

    public ReadOnlySpan<VisualProbeFeature> Features
    {
        get
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return _features is null ? [] : _features.AsSpan(0, _featureCount);
        }
    }

    public static VisualProbeFeatureLease Create(
        VisualProbeObservation observation,
        ReadOnlySpan<VisualProbeFeature> features,
        ArrayPool<VisualProbeFeature>? pool = null) =>
        new(observation, features, pool ?? ArrayPool<VisualProbeFeature>.Shared);

    public VisualProbeFeature[] CopyFeatures() => Features.ToArray();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        var features = Interlocked.Exchange(ref _features, null);
        if (features is null) return;
        Array.Clear(features);
        _pool.Return(features, clearArray: false);
    }
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

internal interface IVisualProbeEvidenceSink
{
    ValueTask WriteAsync(
        AnonymousVisualEvidenceInterval interval,
        CancellationToken cancellationToken);
}
