namespace Trazio.AsistenteReunion.App;

internal sealed class SessionTimeline
{
    private readonly TimeProvider _timeProvider;
    private readonly long _originTimestamp;
    private long _lastOffsetTicks;

    public SessionTimeline(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _originTimestamp = _timeProvider.GetTimestamp();
        OriginUtc = _timeProvider.GetUtcNow().ToUniversalTime();
    }

    public DateTimeOffset OriginUtc { get; }

    public TimeSpan GetCurrentOffset()
    {
        var elapsed = _timeProvider.GetElapsedTime(_originTimestamp, _timeProvider.GetTimestamp());
        var candidateTicks = Math.Max(0, elapsed.Ticks);

        while (true)
        {
            var priorTicks = Interlocked.Read(ref _lastOffsetTicks);
            if (candidateTicks <= priorTicks) return TimeSpan.FromTicks(priorTicks);
            if (Interlocked.CompareExchange(ref _lastOffsetTicks, candidateTicks, priorTicks) == priorTicks)
                return TimeSpan.FromTicks(candidateTicks);
        }
    }
}

internal sealed class VisualProbeRateGate
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromMilliseconds(500);

    private readonly SessionTimeline _timeline;
    private readonly TimeSpan _interval;
    private readonly object _gate = new();
    private TimeSpan? _lastAcceptedOffset;

    public VisualProbeRateGate(SessionTimeline timeline, TimeSpan? interval = null)
    {
        _timeline = timeline ?? throw new ArgumentNullException(nameof(timeline));
        _interval = interval ?? DefaultInterval;
        if (_interval < MinimumInterval)
            throw new ArgumentOutOfRangeException(nameof(interval), "Sampling cannot exceed two observations per second.");
    }

    public bool TryAcquire(out TimeSpan offset)
    {
        offset = _timeline.GetCurrentOffset();
        lock (_gate)
        {
            if (_lastAcceptedOffset is { } prior && offset - prior < _interval) return false;
            _lastAcceptedOffset = offset;
            return true;
        }
    }
}
