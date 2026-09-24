using System.Globalization;
using System.Text;

namespace Trazio.AsistenteReunion.Core;

public enum HistorySearchMatchKind
{
    SessionTitle,
    TranscriptSegment
}

public sealed record HistorySearchHit(
    string SessionId,
    string SessionTitle,
    DateTimeOffset SessionStartedAt,
    string? SegmentId,
    AudioSourceKind? Source,
    TimeSpan? Start,
    string Snippet,
    HistorySearchMatchKind MatchKind);

public sealed record HistorySearchResult(
    IReadOnlyList<HistorySearchHit> Hits,
    int MatchCount,
    bool IsTruncated);

public static class HistorySearchText
{
    public const int MinimumQueryLength = 2;
    public const int MaximumQueryLength = 120;
    public const int MaximumResults = 100;

    public static string PrepareQuery(string? query)
    {
        var prepared = (query ?? string.Empty).Trim();
        if (prepared.Length > MaximumQueryLength)
            throw new ArgumentException($"La búsqueda no puede superar {MaximumQueryLength} caracteres.", nameof(query));
        if (Normalize(prepared).Length < MinimumQueryLength)
            throw new ArgumentException($"Escribe al menos {MinimumQueryLength} caracteres para buscar.", nameof(query));
        return prepared;
    }

    public static bool Contains(string value, string normalizedQuery) =>
        Normalize(value).Contains(normalizedQuery, StringComparison.Ordinal);

    public static string Normalize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var normalized = new StringBuilder(decomposed.Length);
        foreach (var rune in decomposed.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.NonSpacingMark or
                UnicodeCategory.SpacingCombiningMark or
                UnicodeCategory.EnclosingMark)
                continue;
            normalized.Append(rune.ToString().ToUpperInvariant());
        }
        return normalized.ToString().Normalize(NormalizationForm.FormC);
    }

    public static string CreateSnippet(string value, int maximumLength = 120)
        => CreateSnippet(value, normalizedQuery: null, maximumLength);

    public static string CreateSnippet(string value, string? normalizedQuery, int maximumLength = 120)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumLength, 1);
        var compact = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (compact.Length <= maximumLength) return compact;
        var matchIndex = string.IsNullOrEmpty(normalizedQuery)
            ? 0
            : Normalize(compact).IndexOf(normalizedQuery, StringComparison.Ordinal);
        var contentLength = Math.Max(1, maximumLength - 2);
        var start = matchIndex <= 0 ? 0 : Math.Max(0, matchIndex - contentLength / 3);
        start = Math.Min(start, Math.Max(0, compact.Length - contentLength));
        var length = Math.Min(contentLength, compact.Length - start);
        var prefix = start > 0 ? "…" : string.Empty;
        var suffix = start + length < compact.Length ? "…" : string.Empty;
        return prefix + compact.Substring(start, length).Trim() + suffix;
    }
}
