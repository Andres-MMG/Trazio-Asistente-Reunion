using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.App;

public sealed record PlaybackTimelineSpan(
    ArchivedAudioChunk Chunk,
    TimeSpan SessionStart,
    TimeSpan SessionEnd,
    TimeSpan OffsetIntoChunk,
    TimeSpan MediaStart,
    TimeSpan MediaEnd);

public sealed class PlaybackTimelineMap
{
    private readonly PlaybackTimelineSpan[] _spans;

    private PlaybackTimelineMap(PlaybackTimelineSpan[] spans)
    {
        _spans = spans;
    }

    public IReadOnlyList<PlaybackTimelineSpan> Spans => _spans;
    public TimeSpan MediaDuration => _spans.Length == 0 ? TimeSpan.Zero : _spans[^1].MediaEnd;
    public TimeSpan TimelineEnd => _spans.Length == 0 ? TimeSpan.Zero : _spans.Max(span => span.SessionEnd);

    public static PlaybackTimelineMap Create(
        DateTimeOffset sessionStartedAt,
        IReadOnlyList<ArchivedAudioChunk> chunks,
        TimeSpan? windowStart = null,
        TimeSpan? windowEnd = null)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        var lowerBound = windowStart ?? TimeSpan.MinValue;
        var upperBound = windowEnd ?? TimeSpan.MaxValue;
        if (upperBound <= lowerBound) return new([]);

        var mediaCursor = TimeSpan.Zero;
        var coveredUntil = lowerBound;
        var spans = new List<PlaybackTimelineSpan>(chunks.Count);
        foreach (var chunk in chunks.OrderBy(item => item.StartedAt).ThenBy(item => item.Sequence))
        {
            if (chunk.Duration <= TimeSpan.Zero) continue;
            var chunkStart = chunk.StartedAt - sessionStartedAt;
            var chunkEnd = chunkStart + chunk.Duration;
            var clippedStart = chunkStart > lowerBound ? chunkStart : lowerBound;
            if (clippedStart < coveredUntil) clippedStart = coveredUntil;
            var clippedEnd = chunkEnd < upperBound ? chunkEnd : upperBound;
            if (clippedEnd <= clippedStart) continue;

            var playableDuration = clippedEnd - clippedStart;
            spans.Add(new(
                chunk,
                clippedStart,
                clippedEnd,
                clippedStart - chunkStart,
                mediaCursor,
                mediaCursor + playableDuration));
            mediaCursor += playableDuration;
            coveredUntil = clippedEnd;
        }

        return new(spans.ToArray());
    }

    public TimeSpan? ResolvePlayablePosition(TimeSpan requested)
    {
        foreach (var span in _spans)
        {
            if (requested < span.SessionStart) return span.SessionStart;
            if (requested >= span.SessionStart && requested < span.SessionEnd) return requested;
        }

        return null;
    }

    public TimeSpan PositionAtElapsed(TimeSpan elapsed)
    {
        if (_spans.Length == 0) return TimeSpan.Zero;
        if (elapsed <= TimeSpan.Zero) return _spans[0].SessionStart;
        if (elapsed >= MediaDuration) return _spans[^1].SessionEnd;

        foreach (var span in _spans)
        {
            if (elapsed < span.MediaEnd)
                return span.SessionStart + (elapsed - span.MediaStart);
        }

        return _spans[^1].SessionEnd;
    }
}
