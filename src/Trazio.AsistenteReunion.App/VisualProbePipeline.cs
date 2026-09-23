namespace Trazio.AsistenteReunion.App;

internal sealed class VisualProbePipeline : IAsyncDisposable
{
    private readonly IVisualProbeDetector _detector;
    private readonly IVisualProbeEvidenceSink _sink;
    private readonly BoundedDropOldestProcessor<VisualProbeFeatureLease> _processor = new();
    private readonly object _writeGate = new();
    private int _completionRequested;
    private long _detectorFailureCount;
    private long _sinkFailureCount;

    public VisualProbePipeline(
        IVisualProbeDetector detector,
        IVisualProbeEvidenceSink sink)
    {
        _detector = detector ?? throw new ArgumentNullException(nameof(detector));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
    }

    public long DroppedCount => _processor.DroppedCount;
    public long DetectorFailureCount => Interlocked.Read(ref _detectorFailureCount);
    public long SinkFailureCount => Interlocked.Read(ref _sinkFailureCount);

    public bool TryWrite(VisualProbeFeatureLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lock (_writeGate)
        {
            if (_completionRequested == 0) return _processor.TryWrite(lease);
            DisposeSafely(lease);
            return false;
        }
    }

    public bool CompleteAt(TimeSpan offset)
    {
        if (offset < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(offset));
        lock (_writeGate)
        {
            if (_completionRequested != 0) return false;
            _completionRequested = 1;

            var completion = VisualProbeFeatureLease.Create(
                VisualProbeObservation.Completed(offset),
                []);
            var accepted = _processor.TryWrite(completion);
            _processor.Complete();
            return accepted;
        }
    }

    public Task RunAsync(CancellationToken cancellationToken = default) =>
        _processor.RunAsync(ProcessAsync, cancellationToken);

    public ValueTask DisposeAsync() => _processor.DisposeAsync();

    private async ValueTask ProcessAsync(
        VisualProbeFeatureLease lease,
        CancellationToken cancellationToken)
    {
        VisualProbeDetectionResult result;
        try
        {
            result = _detector.Observe(lease.Observation, lease.Features);
        }
        catch
        {
            Interlocked.Increment(ref _detectorFailureCount);
            return;
        }

        foreach (var interval in result.Intervals)
        {
            try
            {
                await _sink.WriteAsync(interval, cancellationToken);
            }
            catch
            {
                Interlocked.Increment(ref _sinkFailureCount);
            }
        }
    }

    private static void DisposeSafely(IDisposable lease)
    {
        try
        {
            lease.Dispose();
        }
        catch
        {
            // A rejected visual lease cannot be allowed to affect unrelated processing.
        }
    }
}
