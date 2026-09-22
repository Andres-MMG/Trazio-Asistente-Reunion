namespace Trazio.AsistenteReunion.App;

public sealed record ComparisonTextSegment(TimeSpan Start, TimeSpan End, string Text);

public sealed record TranscriptComparisonRow(
    TimeSpan Start,
    TimeSpan End,
    string LeftText,
    string RightText)
{
    public string TimeLabel => Start.ToString(@"hh\:mm\:ss");
    public bool HasAudio { get; init; }
}

public static class TranscriptComparison
{
    public static IReadOnlyList<TranscriptComparisonRow> Align(
        IEnumerable<ComparisonTextSegment> left,
        IEnumerable<ComparisonTextSegment> right,
        TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval));

        var windows = new SortedDictionary<long, ComparisonWindow>();
        Add(left, isLeft: true);
        Add(right, isLeft: false);
        return windows.Select(pair => new TranscriptComparisonRow(
            TimeSpan.FromTicks(checked(pair.Key * interval.Ticks)),
            TimeSpan.FromTicks(checked((pair.Key + 1) * interval.Ticks)),
            string.Join(" ", pair.Value.Left),
            string.Join(" ", pair.Value.Right))).ToArray();

        void Add(IEnumerable<ComparisonTextSegment> segments, bool isLeft)
        {
            foreach (var segment in segments.OrderBy(item => item.Start).ThenBy(item => item.End))
            {
                if (string.IsNullOrWhiteSpace(segment.Text)) continue;
                var index = Math.Max(0, segment.Start.Ticks / interval.Ticks);
                if (!windows.TryGetValue(index, out var window))
                {
                    window = new ComparisonWindow();
                    windows.Add(index, window);
                }
                (isLeft ? window.Left : window.Right).Add(segment.Text.Trim());
            }
        }
    }

    private sealed class ComparisonWindow
    {
        public List<string> Left { get; } = [];
        public List<string> Right { get; } = [];
    }
}
