using Trazio.AsistenteReunion.App;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.Tests;

public sealed class PlaybackTimelineMapTests
{
    private static readonly DateTimeOffset SessionStartedAt = new(2026, 9, 24, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PositionAtElapsed_CrossesGapWithoutCompressingSessionTimeline()
    {
        var map = PlaybackTimelineMap.Create(SessionStartedAt,
        [
            Chunk("first", 0, 0, 10),
            Chunk("second", 1, 20, 10)
        ]);

        Assert.Equal(TimeSpan.FromSeconds(9), map.PositionAtElapsed(TimeSpan.FromSeconds(9)));
        Assert.Equal(TimeSpan.FromSeconds(20), map.PositionAtElapsed(TimeSpan.FromSeconds(10)));
        Assert.Equal(TimeSpan.FromSeconds(25), map.PositionAtElapsed(TimeSpan.FromSeconds(15)));
        Assert.Equal(TimeSpan.FromSeconds(30), map.PositionAtElapsed(TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public void ResolvePlayablePosition_GapAndBoundariesUseNextStartAndExclusiveEnd()
    {
        var map = PlaybackTimelineMap.Create(SessionStartedAt,
        [
            Chunk("first", 0, 2, 8),
            Chunk("second", 1, 20, 10)
        ]);

        Assert.Equal(TimeSpan.FromSeconds(2), map.ResolvePlayablePosition(TimeSpan.Zero));
        Assert.Equal(TimeSpan.FromSeconds(9), map.ResolvePlayablePosition(TimeSpan.FromSeconds(9)));
        Assert.Equal(TimeSpan.FromSeconds(20), map.ResolvePlayablePosition(TimeSpan.FromSeconds(10)));
        Assert.Null(map.ResolvePlayablePosition(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void CreatePlan_WindowAcrossGap_BudgetsOnlyPlayableAudioAndStopsAtSegmentEnd()
    {
        var plan = SegmentAudioNavigator.CreatePlan(
            SessionStartedAt,
            TimeSpan.FromSeconds(5),
            [Chunk("first", 0, 0, 10), Chunk("second", 1, 20, 10)],
            TimeSpan.FromSeconds(18));

        Assert.NotNull(plan);
        Assert.Equal(new[] { "first", "second" }, plan.Chunks.Select(item => item.Id).ToArray());
        Assert.Equal(TimeSpan.FromSeconds(5), plan.OffsetIntoFirstChunk);
        Assert.Equal(TimeSpan.FromSeconds(8), plan.MaximumDuration);
        Assert.Equal(TimeSpan.FromSeconds(5), plan.StartPosition);
        Assert.Equal(TimeSpan.FromSeconds(20), plan.Timeline.PositionAtElapsed(TimeSpan.FromSeconds(5)));
        Assert.Equal(TimeSpan.FromSeconds(23), plan.Timeline.PositionAtElapsed(plan.MaximumDuration));
    }

    [Fact]
    public void CreatePlan_StartInsideGap_JumpsToNextChunkWithoutExtendingWindow()
    {
        var chunks = new[] { Chunk("next", 0, 20, 10) };

        Assert.Null(SegmentAudioNavigator.CreatePlan(
            SessionStartedAt,
            TimeSpan.FromSeconds(5),
            chunks,
            TimeSpan.FromSeconds(10)));

        var plan = SegmentAudioNavigator.CreatePlan(
            SessionStartedAt,
            TimeSpan.FromSeconds(5),
            chunks,
            TimeSpan.FromSeconds(20));

        Assert.NotNull(plan);
        Assert.Equal(TimeSpan.FromSeconds(20), plan.StartPosition);
        Assert.Equal(TimeSpan.FromSeconds(5), plan.MaximumDuration);
        Assert.Equal(TimeSpan.FromSeconds(25), plan.Timeline.PositionAtElapsed(plan.MaximumDuration));
    }

    [Fact]
    public void CreatePlan_EndBoundary_DoesNotIncludeLaterAudio()
    {
        var plan = SegmentAudioNavigator.CreatePlan(
            SessionStartedAt,
            TimeSpan.Zero,
            [Chunk("inside", 0, 0, 10), Chunk("after", 1, 10, 10)],
            TimeSpan.FromSeconds(10));

        Assert.NotNull(plan);
        Assert.Single(plan.Chunks);
        Assert.Equal("inside", plan.Chunks[0].Id);
        Assert.Equal(TimeSpan.FromSeconds(10), plan.MaximumDuration);
    }

    [Fact]
    public void Create_OverlappingChunks_TrimsLaterChunkAndNeverMovesTimelineBackward()
    {
        var chunks = new[]
        {
            Chunk("first", 0, 0, 10),
            Chunk("overlap", 1, 5, 10)
        };

        var map = PlaybackTimelineMap.Create(SessionStartedAt, chunks);
        var plan = SegmentAudioNavigator.CreatePlan(
            SessionStartedAt,
            TimeSpan.Zero,
            chunks,
            TimeSpan.FromSeconds(15));

        Assert.Equal(TimeSpan.FromSeconds(15), map.MediaDuration);
        Assert.Equal(TimeSpan.FromSeconds(10), map.PositionAtElapsed(TimeSpan.FromSeconds(10)));
        Assert.Equal(TimeSpan.FromSeconds(12), map.PositionAtElapsed(TimeSpan.FromSeconds(12)));
        Assert.NotNull(plan);
        Assert.Equal(TimeSpan.FromSeconds(5), plan.Timeline.Spans[1].OffsetIntoChunk);
        Assert.Equal(TimeSpan.FromSeconds(5), plan.Timeline.Spans[1].SessionEnd - plan.Timeline.Spans[1].SessionStart);
    }

    private static ArchivedAudioChunk Chunk(string id, long sequence, int start, int duration) =>
        new(id, "session", AudioSourceKind.SystemOutput, sequence, SessionStartedAt.AddSeconds(start),
            TimeSpan.FromSeconds(duration), $"{id}.wav.enc", duration * 32_000L);
}
