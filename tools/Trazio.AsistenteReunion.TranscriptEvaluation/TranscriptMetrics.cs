using System.Globalization;
using System.Text;

namespace Trazio.AsistenteReunion.TranscriptEvaluation;

public static class TranscriptMetrics
{
    public const string NormalizationVersion = "es-cl-v1-lowercase-nfc-alphanumeric";
    public const int MaxTextCharacters = 4_096;
    public const long MaxComparisonCellsPerPair = 2_000_000;
    public const long MaxComparisonCellsPerManifest = 50_000_000;

    public static long EstimateComparisonCells(string reference, string hypothesis)
    {
        if (reference.Length is 0 or > MaxTextCharacters || hypothesis.Length is 0 or > MaxTextCharacters)
            throw new EvaluationException("El texto supera el límite de 4096 caracteres por fragmento.");
        var estimatedCells = 2L * reference.Length * hypothesis.Length;
        if (estimatedCells > MaxComparisonCellsPerPair)
            throw new EvaluationException("La comparación de este fragmento supera el límite de cálculo.");
        return estimatedCells;
    }


    public static string Normalize(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormC).ToLowerInvariant();
        var result = new StringBuilder(normalized.Length);
        var pendingSpace = false;
        foreach (var rune in normalized.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (Rune.IsLetterOrDigit(rune) || category == UnicodeCategory.NonSpacingMark)
            {
                if (pendingSpace && result.Length > 0)
                    result.Append(' ');
                result.Append(rune.ToString());
                pendingSpace = false;
            }
            else
            {
                pendingSpace = true;
            }
        }

        return result.ToString();
    }

    public static (int WordErrors, int ReferenceWords, int CharacterErrors, int ReferenceCharacters)
        CountErrors(string reference, string hypothesis)
    {
        EstimateComparisonCells(reference, hypothesis);
        var referenceNormalized = Normalize(reference);
        var hypothesisNormalized = Normalize(hypothesis);
        var referenceWords = referenceNormalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var hypothesisWords = hypothesisNormalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var referenceCharacters = referenceNormalized.Replace(" ", string.Empty, StringComparison.Ordinal).EnumerateRunes().ToArray();
        var hypothesisCharacters = hypothesisNormalized.Replace(" ", string.Empty, StringComparison.Ordinal).EnumerateRunes().ToArray();
        if ((long)referenceWords.Length * hypothesisWords.Length > MaxComparisonCellsPerPair ||
            (long)referenceCharacters.Length * hypothesisCharacters.Length > MaxComparisonCellsPerPair)
            throw new EvaluationException("La comparación normalizada supera el límite de cálculo.");
        return (
            EditDistance(referenceWords, hypothesisWords),
            referenceWords.Length,
            EditDistance(referenceCharacters, hypothesisCharacters),
            referenceCharacters.Length);
    }

    public static int EditDistance<T>(IReadOnlyList<T> reference, IReadOnlyList<T> hypothesis)
    {
        var previous = new int[hypothesis.Count + 1];
        var current = new int[hypothesis.Count + 1];
        for (var j = 0; j <= hypothesis.Count; j++)
            previous[j] = j;

        for (var i = 1; i <= reference.Count; i++)
        {
            current[0] = i;
            for (var j = 1; j <= hypothesis.Count; j++)
                current[j] = EqualityComparer<T>.Default.Equals(reference[i - 1], hypothesis[j - 1])
                    ? previous[j - 1]
                    : 1 + Math.Min(previous[j - 1], Math.Min(previous[j], current[j - 1]));
            (current, previous) = (previous, current);
        }

        return previous[hypothesis.Count];
    }

    public static double Percentile(IReadOnlyList<double> values, double percentile)
    {
        if (values.Count == 0 || percentile is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(values));
        var sorted = values.Order().ToArray();
        var rank = percentile * (sorted.Length - 1);
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (rank - lower);
    }
}
