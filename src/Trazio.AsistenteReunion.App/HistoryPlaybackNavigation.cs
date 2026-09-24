using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.App;

public sealed record HistoryPlaybackNavigationState(
    string? PreviousId,
    string? NextId)
{
    public static HistoryPlaybackNavigationState Empty { get; } = new(null, null);
    public bool CanPrevious => PreviousId is not null;
    public bool CanNext => NextId is not null;
}

public static class HistoryPlaybackNavigation
{
    public static HistoryPlaybackNavigationState Create(
        IReadOnlyList<string> visibleSegmentIds,
        string? selectedSegmentId)
    {
        ArgumentNullException.ThrowIfNull(visibleSegmentIds);
        if (string.IsNullOrEmpty(selectedSegmentId)) return HistoryPlaybackNavigationState.Empty;

        var selectedIndex = -1;
        for (var index = 0; index < visibleSegmentIds.Count; index++)
        {
            if (!string.Equals(visibleSegmentIds[index], selectedSegmentId, StringComparison.Ordinal)) continue;
            selectedIndex = index;
            break;
        }

        if (selectedIndex < 0) return HistoryPlaybackNavigationState.Empty;
        return new(
            selectedIndex > 0 ? visibleSegmentIds[selectedIndex - 1] : null,
            selectedIndex + 1 < visibleSegmentIds.Count ? visibleSegmentIds[selectedIndex + 1] : null);
    }
}

public static class HistoryPlaybackAvailability
{
    public static bool CanBeginOperation(bool deleteInProgress, bool closing) =>
        !deleteInProgress && !closing;
}

public sealed class PlaybackTransitionGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task RunAsync(Func<Task> transition)
    {
        ArgumentNullException.ThrowIfNull(transition);
        await _gate.WaitAsync();
        try { await transition(); }
        finally { _gate.Release(); }
    }

    public async Task<T> RunAsync<T>(Func<Task<T>> transition)
    {
        ArgumentNullException.ThrowIfNull(transition);
        await _gate.WaitAsync();
        try { return await transition(); }
        finally { _gate.Release(); }
    }
}

public sealed class PlaybackTransitionEpoch
{
    private long _value;
    public long Advance() => Interlocked.Increment(ref _value);
    public bool IsCurrent(long value) => Volatile.Read(ref _value) == value;
}

public static class HistoryPlaybackHighlight
{
    public static string? ResolveSegmentId(
        IReadOnlyList<TranscriptSegment> visibleSegments,
        AudioSourceKind source,
        TimeSpan position) =>
        ResolveItem(visibleSegments, static segment => segment, source, position)?.Id;

    public static T? ResolveItem<T>(
        IReadOnlyList<T> visibleItems,
        Func<T, TranscriptSegment> segmentSelector,
        AudioSourceKind source,
        TimeSpan position)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(visibleItems);
        ArgumentNullException.ThrowIfNull(segmentSelector);
        T? bestItem = default;
        TranscriptSegment? bestSegment = null;
        foreach (var item in visibleItems)
        {
            var segment = segmentSelector(item);
            if (segment.Source != source || segment.Start > position || position >= segment.End) continue;
            if (bestSegment is not null &&
                (segment.Start < bestSegment.Start ||
                 (segment.Start == bestSegment.Start && segment.Sequence < bestSegment.Sequence) ||
                 (segment.Start == bestSegment.Start && segment.Sequence == bestSegment.Sequence &&
                  string.CompareOrdinal(segment.Id, bestSegment.Id) >= 0))) continue;
            bestItem = item;
            bestSegment = segment;
        }
        return bestItem;
    }
}
