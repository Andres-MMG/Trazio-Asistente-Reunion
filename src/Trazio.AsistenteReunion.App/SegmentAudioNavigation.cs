using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.App;

public sealed record SegmentPlaybackPlan(
    IReadOnlyList<ArchivedAudioChunk> Chunks,
    TimeSpan OffsetIntoFirstChunk,
    TimeSpan MaximumDuration);

public static class SegmentAudioNavigator
{
    public static SegmentPlaybackPlan? CreatePlan(
        DateTimeOffset sessionStartedAt,
        TimeSpan segmentStart,
        IReadOnlyList<ArchivedAudioChunk> chunks,
        TimeSpan? replayDuration = null)
    {
        var ordered = chunks.OrderBy(chunk => chunk.StartedAt).ThenBy(chunk => chunk.Sequence).ToArray();
        if (ordered.Length == 0) return null;
        var target = sessionStartedAt + segmentStart;
        var index = Array.FindIndex(ordered, chunk => target >= chunk.StartedAt && target < chunk.StartedAt + chunk.Duration);
        if (index < 0) return null;
        return new(
            ordered[index..],
            target - ordered[index].StartedAt,
            replayDuration ?? TimeSpan.FromSeconds(8));
    }
}