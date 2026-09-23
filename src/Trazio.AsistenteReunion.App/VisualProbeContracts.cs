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
    {
        if (activityScore is < VisualProbeDetectionPolicy.MinimumScore or > VisualProbeDetectionPolicy.MaximumScore)
            throw new ArgumentOutOfRangeException(nameof(activityScore));
        ActivityScore = activityScore;
    }

    public int ActivityScore { get; }
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
