using Trazio.AsistenteReunion.App;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.Tests;

public sealed class SegmentAudioNavigatorTests
{
    [Fact]
    public void CreatePlan_WithTimestampInsideChunk_MapsOffsetAndShortReplay()
    {
        var started = new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
        var chunks = new[]
        {
            Chunk("one", 0, started, 30),
            Chunk("two", 1, started.AddSeconds(30), 30)
        };

        var plan = SegmentAudioNavigator.CreatePlan(started, TimeSpan.FromSeconds(37), chunks);

        Assert.NotNull(plan);
        Assert.Equal("two", plan.Chunks[0].Id);
        Assert.Equal(TimeSpan.FromSeconds(7), plan.OffsetIntoFirstChunk);
        Assert.Equal(TimeSpan.FromSeconds(8), plan.MaximumDuration);
    }

    [Fact]
    public void CreatePlan_WithoutRetainedAudioAtTimestamp_ReturnsNull()
    {
        var started = DateTimeOffset.UtcNow;
        var chunks = new[] { Chunk("late", 0, started.AddSeconds(30), 30) };

        Assert.Null(SegmentAudioNavigator.CreatePlan(started, TimeSpan.FromSeconds(5), chunks));
        Assert.Null(SegmentAudioNavigator.CreatePlan(started, TimeSpan.FromSeconds(90), chunks));
    }

    [Fact]
    public void GetTimelineDuration_UsesLatestChunkEndRelativeToSessionStart()
    {
        var started = new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
        var chunks = new[]
        {
            Chunk("first", 0, started.AddSeconds(2), 8),
            Chunk("last", 1, started.AddSeconds(15), 5)
        };

        Assert.Equal(
            TimeSpan.FromSeconds(20),
            AudioWaveformBuilder.GetTimelineDuration(started, chunks));
    }

    [Fact]
    public void GetTimelineDuration_WithoutChunks_IsZero()
    {
        Assert.Equal(
            TimeSpan.Zero,
            AudioWaveformBuilder.GetTimelineDuration(DateTimeOffset.UtcNow, []));
    }
    private static ArchivedAudioChunk Chunk(string id, long sequence, DateTimeOffset started, int seconds) =>
        new(id, "session", AudioSourceKind.Microphone, sequence, started,
            TimeSpan.FromSeconds(seconds), $"{id}.wav.enc", 100);
}