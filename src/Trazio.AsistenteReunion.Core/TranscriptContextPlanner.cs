namespace Trazio.AsistenteReunion.Core;

public sealed record TranscriptContextItem(
    TimeSpan Start,
    TimeSpan End,
    string Text,
    bool HumanApproved);

public sealed record TranscriptContextPacket(
    IReadOnlyList<TranscriptContextItem> Previous,
    IReadOnlyList<TranscriptContextItem> Following,
    IReadOnlyList<string> GlossaryTerms);

public static class TranscriptContextPlanner
{
    public const int DefaultMaximumSegmentsPerSide = 3;
    public const int DefaultMaximumCharacters = 4_000;

    public static TranscriptContextPacket Build(
        IEnumerable<TranscriptContextItem> timeline,
        TimeSpan disputedStart,
        TimeSpan disputedEnd,
        IEnumerable<string> glossaryTerms,
        int maximumSegmentsPerSide = DefaultMaximumSegmentsPerSide,
        int maximumCharacters = DefaultMaximumCharacters)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(glossaryTerms);
        if (disputedStart < TimeSpan.Zero || disputedEnd <= disputedStart)
            throw new ArgumentOutOfRangeException(nameof(disputedEnd));
        if (maximumSegmentsPerSide is < 1 or > 12)
            throw new ArgumentOutOfRangeException(nameof(maximumSegmentsPerSide));
        if (maximumCharacters is < 128 or > 16_000)
            throw new ArgumentOutOfRangeException(nameof(maximumCharacters));

        var usable = timeline
            .Where(item => !string.IsNullOrWhiteSpace(item.Text))
            .OrderBy(item => item.Start)
            .ThenBy(item => item.End)
            .ToArray();
        var previous = usable
            .Where(item => item.End <= disputedStart)
            .TakeLast(maximumSegmentsPerSide)
            .ToList();
        var following = usable
            .Where(item => item.Start >= disputedEnd)
            .Take(maximumSegmentsPerSide)
            .ToList();
        var terms = glossaryTerms
            .Select(item => item.Trim())
            .Where(item => item.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Take(64)
            .ToList();

        while (CharacterCount(previous, following, terms) > maximumCharacters)
        {
            if (previous.Count > 0)
                previous.RemoveAt(0);
            else if (following.Count > 0)
                following.RemoveAt(following.Count - 1);
            else if (terms.Count > 0)
                terms.RemoveAt(terms.Count - 1);
            else
                break;
        }

        return new(previous, following, terms);
    }

    private static int CharacterCount(
        IEnumerable<TranscriptContextItem> previous,
        IEnumerable<TranscriptContextItem> following,
        IEnumerable<string> terms) =>
        previous.Sum(item => item.Text.Length) +
        following.Sum(item => item.Text.Length) +
        terms.Sum(item => item.Length);
}
