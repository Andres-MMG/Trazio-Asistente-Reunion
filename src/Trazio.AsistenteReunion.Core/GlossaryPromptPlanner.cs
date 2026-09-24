using System.Security.Cryptography;
using System.Text;

namespace Trazio.AsistenteReunion.Core;

public sealed record GlossaryPromptItem(
    string MistakenForm,
    string PreferredTerm,
    string Category);

public sealed record GlossaryPromptPlan(
    string? Prompt,
    string Version,
    IReadOnlyList<GlossaryPromptItem> Included,
    int ActiveCount,
    int ExcludedAmbiguousCount,
    int ExcludedByLimitCount)
{
    public const string NoGlossaryVersion = "none";

    public static GlossaryPromptPlan None { get; } =
        new(null, NoGlossaryVersion, [], 0, 0, 0);

    public bool HasPrompt => !string.IsNullOrWhiteSpace(Prompt);
}

public static class GlossaryPromptPlanner
{
    public const int MaximumIncludedTerms = 64;
    public const int MaximumPromptCharacters = 1_024;
    private const string PromptPrefix = "Vocabulario confirmado para esta transcripción: ";

    public static GlossaryPromptPlan Create(IReadOnlyList<GlossaryEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var active = entries
            .Where(entry => entry.IsActive)
            .OrderBy(entry => GlossaryExchangePlanner.NormalizeForComparison(entry.MistakenForm), StringComparer.Ordinal)
            .ThenBy(entry => GlossaryExchangePlanner.NormalizeForComparison(entry.PreferredTerm), StringComparer.Ordinal)
            .ThenBy(entry => entry.Id, StringComparer.Ordinal)
            .ToArray();
        if (active.Length == 0) return GlossaryPromptPlan.None;

        var ambiguousIds = active
            .GroupBy(entry => GlossaryExchangePlanner.NormalizeForComparison(entry.MistakenForm), StringComparer.Ordinal)
            .Where(group => group
                .Select(entry => GlossaryExchangePlanner.NormalizeForComparison(entry.PreferredTerm))
                .Distinct(StringComparer.Ordinal)
                .Skip(1)
                .Any())
            .SelectMany(group => group.Select(entry => entry.Id))
            .ToHashSet(StringComparer.Ordinal);

        var candidates = active
            .Where(entry => !ambiguousIds.Contains(entry.Id))
            .GroupBy(entry => GlossaryExchangePlanner.NormalizeForComparison(entry.PreferredTerm), StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(entry => GlossaryExchangePlanner.NormalizeForComparison(entry.PreferredTerm), StringComparer.Ordinal)
            .ThenBy(entry => entry.Id, StringComparer.Ordinal)
            .ToArray();

        var included = new List<GlossaryPromptItem>(Math.Min(candidates.Length, MaximumIncludedTerms));
        var prompt = new StringBuilder(PromptPrefix);
        foreach (var entry in candidates)
        {
            if (included.Count >= MaximumIncludedTerms) break;
            var separator = included.Count == 0 ? string.Empty : ", ";
            var suffix = ".";
            if (prompt.Length + separator.Length + entry.PreferredTerm.Length + suffix.Length > MaximumPromptCharacters)
                break;

            prompt.Append(separator).Append(entry.PreferredTerm);
            included.Add(new(entry.MistakenForm, entry.PreferredTerm, entry.Category));
        }

        if (included.Count == 0)
            return new(null, GlossaryPromptPlan.NoGlossaryVersion, [], active.Length, ambiguousIds.Count, candidates.Length);

        prompt.Append('.');
        var promptText = prompt.ToString();
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(promptText))).ToLowerInvariant();
        return new(
            promptText,
            $"glossary-v1:{digest}:{included.Count}",
            included,
            active.Length,
            ambiguousIds.Count,
            candidates.Length - included.Count);
    }
}
