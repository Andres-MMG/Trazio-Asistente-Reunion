using Trazio.AsistenteReunion.App;

namespace Trazio.AsistenteReunion.Tests;

public sealed class SessionTimelineTests
{
    [Fact]
    [Trait("Area", "VisualCapture")]
    public void GetCurrentOffset_WhenWallClockChanges_UsesOnlyMonotonicTimestamp()
    {
        var origin = new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.FromHours(-3));
        var time = new ManualTimeProvider(origin);
        var timeline = new SessionTimeline(time);

        time.SetUtcNow(origin.AddDays(-10));
        time.AdvanceTimestamp(TimeSpan.FromSeconds(2));

        Assert.Equal(origin.ToUniversalTime(), timeline.StartedAtUtc);
        Assert.Equal(TimeSpan.FromSeconds(2), timeline.GetCurrentOffset());
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void ToUtc_AfterWallClockChanges_UsesFixedSessionStartAndMonotonicOffset()
    {
        var origin = new DateTimeOffset(2026, 9, 23, 15, 30, 0, TimeSpan.Zero);
        var time = new ManualTimeProvider(origin);
        var timeline = new SessionTimeline(time);
        time.SetUtcNow(origin.AddDays(2));
        time.AdvanceTimestamp(TimeSpan.FromSeconds(7));

        var offset = timeline.GetCurrentOffset();

        Assert.Equal(origin.AddSeconds(7), timeline.ToUtc(offset));
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void GetCurrentOffset_WhenProviderTimestampRegresses_NeverReturnsADecreasingOffset()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var timeline = new SessionTimeline(time);
        time.SetTimestamp(TimeSpan.FromSeconds(5).Ticks);
        var first = timeline.GetCurrentOffset();

        time.SetTimestamp(TimeSpan.FromSeconds(3).Ticks);
        var second = timeline.GetCurrentOffset();

        Assert.Equal(TimeSpan.FromSeconds(5), first);
        Assert.Equal(first, second);
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void TryAcquire_WithDefaultInterval_AllowsAtMostOneSamplePerSecond()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var gate = new VisualProbeRateGate(CreateContext(time));

        Assert.True(gate.TryAcquire(out var first));
        time.AdvanceTimestamp(TimeSpan.FromMilliseconds(999));
        Assert.False(gate.TryAcquire(out var rejected));
        time.AdvanceTimestamp(TimeSpan.FromMilliseconds(1));
        Assert.True(gate.TryAcquire(out var second));

        Assert.Equal(TimeSpan.Zero, first);
        Assert.Equal(TimeSpan.FromMilliseconds(999), rejected);
        Assert.Equal(TimeSpan.FromSeconds(1), second);
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void Constructor_WithIntervalBelowAbsoluteMinimum_RejectsMoreThanTwoSamplesPerSecond()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var context = CreateContext(time);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new VisualProbeRateGate(context, TimeSpan.FromMilliseconds(499)));

        var gate = new VisualProbeRateGate(context, TimeSpan.FromMilliseconds(500));
        Assert.True(gate.TryAcquire(out _));
        time.AdvanceTimestamp(TimeSpan.FromMilliseconds(499));
        Assert.False(gate.TryAcquire(out _));
        time.AdvanceTimestamp(TimeSpan.FromMilliseconds(1));
        Assert.True(gate.TryAcquire(out _));
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void TryAcquire_AfterTimelineContextIsRevoked_FailsClosed()
    {
        var context = CreateContext(new ManualTimeProvider(DateTimeOffset.UtcNow));
        var gate = new VisualProbeRateGate(context);
        context.Revoke();

        var acquired = gate.TryAcquire(out var offset);

        Assert.False(acquired);
        Assert.Equal(TimeSpan.Zero, offset);
    }

    private static SessionTimelineContext CreateContext(TimeProvider timeProvider) =>
        new("timeline-test-session", 1, new SessionTimeline(timeProvider));

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);

        public void AdvanceTimestamp(TimeSpan elapsed) =>
            Interlocked.Add(ref _timestamp, elapsed.Ticks);

        public void SetTimestamp(long timestamp) => Interlocked.Exchange(ref _timestamp, timestamp);

        public void SetUtcNow(DateTimeOffset utcNow) => _utcNow = utcNow;
    }
}
