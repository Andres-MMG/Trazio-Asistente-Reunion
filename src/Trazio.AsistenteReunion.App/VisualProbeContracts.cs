using System.Buffers;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.App;

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


internal interface IVisualProbeEvidenceSink
{
    ValueTask WriteAsync(
        AnonymousVisualEvidenceInterval interval,
        CancellationToken cancellationToken);
}
