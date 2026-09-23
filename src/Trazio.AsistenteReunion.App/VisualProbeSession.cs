using Windows.Graphics.DirectX.Direct3D11;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.App;

internal enum VisualProbeFrameResult
{
    Observed = 0,
    RateLimited = 1,
    ContextUnavailable = 2,
    ProfileUnavailable = 3,
    InvalidProfile = 4,
    ExtractionFailed = 5,
    PipelineUnavailable = 6
}

internal interface IVisualProbeFrameConsumer
{
    bool BeginSurface(out long surfaceRevision);
    VisualProbeFrameResult ObserveFrame(
        long surfaceRevision,
        Func<IDirect3DSurface> getSurface);
    bool MarkUnavailable(long surfaceRevision);
}

internal sealed class VisualProbeSession : IAnonymousVisualAnalysisSession
{
    private readonly object _gate = new();
    private readonly ISessionTimelineContext _timelineContext;
    private readonly VisualProbeRateGate _rateGate;
    private readonly IVisualProbeProfile _profile;
    private readonly IVisualProbeExtractor _extractor;
    private readonly VisualProbePipeline _pipeline;
    private readonly Action<VisualProbeFailureKind>? _failureObserver;
    private readonly Task _runTask;
    private VisualProbeFeatureLease? _pendingObservation;
    private TimeSpan? _lastObservationOffset;
    private Task? _completionTask;
    private long _surfaceRevision;
    private bool _accepting = true;
    private long _extractionFailureCount;

    private VisualProbeSession(
        ISessionTimelineContext timelineContext,
        IVisualProbeProfile profile,
        IVisualProbeExtractor extractor,
        IVisualProbeDetector detector,
        IVisualProbeEvidenceSink sink,
        TimeSpan? samplingInterval,
        Action<VisualProbeFailureKind>? failureObserver)
    {
        _timelineContext = timelineContext;
        _profile = profile;
        _extractor = extractor;
        _failureObserver = failureObserver;
        _rateGate = new(timelineContext, samplingInterval);
        _pipeline = new(detector, sink, NotifyFailure);
        _runTask = _pipeline.RunAsync();

        if (!_rateGate.TryAcquire(out var startOffset) ||
            !TryWriteUnavailableUnderGate(startOffset, surfaceRevision: 0))
        {
            _accepting = false;
            _completionTask = DisposeWithoutCompletionAsync();
            throw new InvalidOperationException("The visual probe requires an active session timeline context.");
        }
    }

    public long DroppedCount => _pipeline.DroppedCount;
    public long DetectorFailureCount => _pipeline.DetectorFailureCount;
    public long SinkFailureCount => _pipeline.SinkFailureCount;
    public long ExtractionFailureCount => Interlocked.Read(ref _extractionFailureCount);

    public static VisualProbeSession Start(
        ISessionTimelineContext timelineContext,
        MeetingProvider provider,
        IVisualProbeEvidenceSink sink,
        TimeSpan? samplingInterval = null)
    {
        ArgumentNullException.ThrowIfNull(timelineContext);
        return Start(
            timelineContext,
            VisualProbeProfiles.GetProduction(provider),
            new D3D11VisualProbeExtractor(),
            new DeterministicVisualActivityDetector(timelineContext.SessionId),
            sink,
            samplingInterval);
    }

    internal static VisualProbeSession Start(
        ISessionTimelineContext timelineContext,
        IVisualProbeProfile profile,
        IVisualProbeExtractor extractor,
        IVisualProbeEvidenceSink sink,
        TimeSpan? samplingInterval = null)
    {
        ArgumentNullException.ThrowIfNull(timelineContext);
        return Start(
            timelineContext,
            profile,
            extractor,
            new DeterministicVisualActivityDetector(timelineContext.SessionId),
            sink,
            samplingInterval);
    }

    internal static VisualProbeSession Start(
        ISessionTimelineContext timelineContext,
        IVisualProbeProfile profile,
        IVisualProbeExtractor extractor,
        IVisualProbeDetector detector,
        IVisualProbeEvidenceSink sink,
        TimeSpan? samplingInterval = null,
        Action<VisualProbeFailureKind>? failureObserver = null)
    {
        ArgumentNullException.ThrowIfNull(timelineContext);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(extractor);
        ArgumentNullException.ThrowIfNull(detector);
        ArgumentNullException.ThrowIfNull(sink);
        if (string.IsNullOrWhiteSpace(timelineContext.SessionId))
            throw new ArgumentException("The timeline context must identify a session.", nameof(timelineContext));

        return new(
            timelineContext,
            profile,
            extractor,
            detector,
            sink,
            samplingInterval,
            failureObserver);
    }

    public bool BeginSurface(out long surfaceRevision)
    {
        lock (_gate)
        {
            surfaceRevision = default;
            if (!_accepting || !_timelineContext.TryGetCurrentOffset(out var offset)) return false;
            if (_surfaceRevision == long.MaxValue) return false;

            surfaceRevision = ++_surfaceRevision;
            TryWriteUnavailableUnderGate(offset, surfaceRevision);
            return true;
        }
    }

    public VisualProbeFrameResult ObserveFrame(
        long surfaceRevision,
        Func<IDirect3DSurface> getSurface)
    {
        ArgumentNullException.ThrowIfNull(getSurface);

        lock (_gate)
        {
            if (!_accepting) return VisualProbeFrameResult.PipelineUnavailable;
            if (!_rateGate.TryAcquire(out var offset))
            {
                return _timelineContext.TryGetCurrentOffset(out _)
                    ? VisualProbeFrameResult.RateLimited
                    : VisualProbeFrameResult.ContextUnavailable;
            }

            if (surfaceRevision <= 0 || surfaceRevision != _surfaceRevision)
                return VisualProbeFrameResult.PipelineUnavailable;
            if (!CanWriteOffset(offset)) return VisualProbeFrameResult.RateLimited;
            if (!_timelineContext.TryGetCurrentOffset(out _))
                return VisualProbeFrameResult.ContextUnavailable;

            if (_profile.ValidationState != VisualProbeProfileValidationState.Validated)
            {
                return TryWriteUnavailableUnderGate(offset, surfaceRevision)
                    ? VisualProbeFrameResult.ProfileUnavailable
                    : VisualProbeFrameResult.PipelineUnavailable;
            }

            if (_profile is not VisualProbeLayoutProfile layoutProfile)
            {
                Interlocked.Increment(ref _extractionFailureCount);
                TryWriteUnavailableUnderGate(offset, surfaceRevision);
                return VisualProbeFrameResult.InvalidProfile;
            }

            try
            {
                var surface = getSurface();
                var lease = _extractor.Extract(surface, layoutProfile, offset, surfaceRevision);
                var status = lease.Observation.Status;
                if (!TryQueueObservationUnderGate(lease))
                    return VisualProbeFrameResult.PipelineUnavailable;
                return status == VisualProbeObservationStatus.Available
                    ? VisualProbeFrameResult.Observed
                    : VisualProbeFrameResult.ProfileUnavailable;
            }
            catch
            {
                Interlocked.Increment(ref _extractionFailureCount);
                NotifyFailure(VisualProbeFailureKind.Extraction);
                TryWriteUnavailableUnderGate(offset, surfaceRevision);
                return VisualProbeFrameResult.ExtractionFailed;
            }
        }
    }

    public bool MarkUnavailable(long surfaceRevision)
    {
        lock (_gate)
        {
            if (!_accepting || surfaceRevision <= 0 || surfaceRevision != _surfaceRevision)
                return false;
            return _timelineContext.TryGetCurrentOffset(out var offset) &&
                   TryWriteUnavailableUnderGate(offset, surfaceRevision);
        }
    }

    public ValueTask CompleteAt(TimeSpan finalOffset)
    {
        if (finalOffset < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(finalOffset));

        lock (_gate)
        {
            if (_completionTask is not null) return new(_completionTask);
            if (_lastObservationOffset is { } last && finalOffset < last)
                throw new ArgumentOutOfRangeException(nameof(finalOffset), "Completion cannot precede the last visual observation.");

            _accepting = false;
            if (_timelineContext.TryGetCurrentOffset(out _))
            {
                FlushPendingObservationUnderGate();
                _completionTask = CompletePipelineAsync(finalOffset);
            }
            else
            {
                _completionTask = DisposeWithoutCompletionAsync(TakePendingObservationUnderGate());
            }
            return new(_completionTask);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_completionTask is not null) return new(_completionTask);

            _accepting = false;
            if (_timelineContext.TryGetCurrentOffset(out var finalOffset))
            {
                FlushPendingObservationUnderGate();
                _completionTask = CompletePipelineAsync(finalOffset);
            }
            else
            {
                _completionTask = DisposeWithoutCompletionAsync(TakePendingObservationUnderGate());
            }
            return new(_completionTask);
        }
    }

    private bool TryWriteUnavailableUnderGate(TimeSpan offset, long surfaceRevision)
    {
        var lease = VisualProbeFeatureLease.Create(
            VisualProbeObservation.Unavailable(offset, surfaceRevision, _profile),
            []);
        return TryQueueObservationUnderGate(lease);
    }

    private bool CanWriteOffset(TimeSpan offset) =>
        _lastObservationOffset is not { } last || offset > last;

    private bool TryQueueObservationUnderGate(VisualProbeFeatureLease lease)
    {
        var offset = lease.Observation.Offset;
        if (_lastObservationOffset is not { } last || offset > last)
        {
            if (!FlushPendingObservationUnderGate())
            {
                DisposeSafely(lease);
                return false;
            }

            _pendingObservation = lease;
            _lastObservationOffset = offset;
            return true;
        }

        if (offset == last &&
            lease.Observation.Status == VisualProbeObservationStatus.Unavailable &&
            _pendingObservation is { } pending)
        {
            if (pending.Observation.Status == VisualProbeObservationStatus.Available)
            {
                _pendingObservation = lease;
                DisposeSafely(pending);
            }
            else
            {
                DisposeSafely(lease);
            }

            return true;
        }

        DisposeSafely(lease);
        return false;
    }

    private bool FlushPendingObservationUnderGate()
    {
        var pending = TakePendingObservationUnderGate();
        return pending is null || _pipeline.TryWrite(pending);
    }

    private VisualProbeFeatureLease? TakePendingObservationUnderGate()
    {
        var pending = _pendingObservation;
        _pendingObservation = null;
        return pending;
    }

    private async Task CompletePipelineAsync(TimeSpan finalOffset)
    {
        try
        {
            _pipeline.CompleteAt(finalOffset);
            await _runTask.ConfigureAwait(false);
        }
        finally
        {
            await _pipeline.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task DisposeWithoutCompletionAsync(VisualProbeFeatureLease? pending = null)
    {
        if (pending is not null) DisposeSafely(pending);
        await _pipeline.DisposeAsync().ConfigureAwait(false);
        try
        {
            await _runTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Revocation must stop the visual branch without inventing a final offset.
        }
    }

    private static void DisposeSafely(IDisposable value)
    {
        try
        {
            value.Dispose();
        }
        catch
        {
            // Probe ownership cleanup remains isolated from capture and audio.
        }
    }

    private void NotifyFailure(VisualProbeFailureKind failure)
    {
        try
        {
            _failureObserver?.Invoke(failure);
        }
        catch
        {
            // Diagnostics must remain isolated from capture and audio.
        }
    }
}
