using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.App;

public sealed record SegmentPlaybackPlan(
    IReadOnlyList<ArchivedAudioChunk> Chunks,
    TimeSpan OffsetIntoFirstChunk,
    TimeSpan MaximumDuration,
    TimeSpan StartPosition,
    PlaybackTimelineMap Timeline);

public static class SegmentAudioNavigator
{
    public static SegmentPlaybackPlan? CreatePlan(
        DateTimeOffset sessionStartedAt,
        TimeSpan segmentStart,
        IReadOnlyList<ArchivedAudioChunk> chunks,
        TimeSpan? replayDuration = null)
    {
        var completeTimeline = PlaybackTimelineMap.Create(sessionStartedAt, chunks);
        var playableStart = completeTimeline.ResolvePlayablePosition(segmentStart);
        if (playableStart is null) return null;

        var requestedDuration = replayDuration ?? TimeSpan.FromSeconds(8);
        if (requestedDuration <= TimeSpan.Zero) return null;
        var requestedEnd = segmentStart + requestedDuration;
        if (playableStart.Value >= requestedEnd) return null;

        var timeline = PlaybackTimelineMap.Create(
            sessionStartedAt,
            chunks,
            playableStart.Value,
            requestedEnd);
        if (timeline.Spans.Count == 0 || timeline.MediaDuration <= TimeSpan.Zero) return null;
        return new(
            timeline.Spans.Select(span => span.Chunk).ToArray(),
            timeline.Spans[0].OffsetIntoChunk,
            timeline.MediaDuration,
            timeline.Spans[0].SessionStart,
            timeline);
    }
}
