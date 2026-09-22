using System.Text.RegularExpressions;

namespace Trazio.AsistenteReunion.Core;

public sealed record GlossaryCandidate(string MistakenForm, string PreferredTerm);

public static partial class GlossaryCandidateExtractor
{
    public static IReadOnlyList<GlossaryCandidate> Extract(string? originalText, string? correctedText)
    {
        var original = Tokenize(originalText);
        var corrected = Tokenize(correctedText);
        if (original.Count == 0 || corrected.Count == 0) return [];

        var costs = BuildCosts(original, corrected);
        var candidates = new List<GlossaryCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var i = original.Count;
        var j = corrected.Count;

        while (i > 0 || j > 0)
        {
            if (i > 0 && j > 0 && Equal(original[i - 1], corrected[j - 1]) && costs[i, j] == costs[i - 1, j - 1])
            {
                i--;
                j--;
                continue;
            }

            if (i > 0 && j > 0 && costs[i, j] == costs[i - 1, j - 1] + 1)
            {
                var mistaken = original[i - 1];
                var preferred = corrected[j - 1];
                if (!Equal(mistaken, preferred) && seen.Add($"{mistaken}\0{preferred}"))
                    candidates.Add(new(mistaken, preferred));
                i--;
                j--;
                continue;
            }

            if (i > 0 && costs[i, j] == costs[i - 1, j] + 1) i--;
            else if (j > 0) j--;
        }

        candidates.Reverse();
        return candidates;
    }

    private static int[,] BuildCosts(IReadOnlyList<string> original, IReadOnlyList<string> corrected)
    {
        var costs = new int[original.Count + 1, corrected.Count + 1];
        for (var i = 0; i <= original.Count; i++) costs[i, 0] = i;
        for (var j = 0; j <= corrected.Count; j++) costs[0, j] = j;

        for (var i = 1; i <= original.Count; i++)
        for (var j = 1; j <= corrected.Count; j++)
        {
            var substitution = costs[i - 1, j - 1] + (Equal(original[i - 1], corrected[j - 1]) ? 0 : 1);
            costs[i, j] = Math.Min(substitution, Math.Min(costs[i - 1, j] + 1, costs[i, j - 1] + 1));
        }
        return costs;
    }

    private static IReadOnlyList<string> Tokenize(string? text) => string.IsNullOrWhiteSpace(text)
        ? []
        : WordRegex().Matches(text).Select(match => match.Value).ToArray();

    private static bool Equal(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"[\p{L}\p{N}]+(?:['’\-][\p{L}\p{N}]+)*", RegexOptions.CultureInvariant)]
    private static partial Regex WordRegex();
}
