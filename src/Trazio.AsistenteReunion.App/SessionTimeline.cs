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
        StartedAtUtc = _timeProvider.GetUtcNow().ToUniversalTime();
    }

    public DateTimeOffset StartedAtUtc { get; }

    public DateTimeOffset ToUtc(TimeSpan offset)
    {
        if (offset < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(offset));
        return StartedAtUtc + offset;
    }

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

internal interface ISessionTimelineContext
{
    string SessionId { get; }
    long Revision { get; }
    DateTimeOffset StartedAtUtc { get; }
    bool TryGetCurrentOffset(out TimeSpan offset);
}

internal sealed class SessionTimelineContext : ISessionTimelineContext
{
    private readonly SessionTimeline _timeline;
    private int _active = 1;

    public SessionTimelineContext(string sessionId, long revision, SessionTimeline timeline)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (revision <= 0) throw new ArgumentOutOfRangeException(nameof(revision));
        _timeline = timeline ?? throw new ArgumentNullException(nameof(timeline));
        SessionId = sessionId;
        Revision = revision;
    }

    public string SessionId { get; }
    public long Revision { get; }
    public DateTimeOffset StartedAtUtc => _timeline.StartedAtUtc;

    public bool TryGetCurrentOffset(out TimeSpan offset)
    {
        offset = default;
        if (Volatile.Read(ref _active) == 0) return false;
        var captured = _timeline.GetCurrentOffset();
        if (Volatile.Read(ref _active) == 0) return false;
        offset = captured;
        return true;
    }

    internal void Revoke() => Interlocked.Exchange(ref _active, 0);
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
