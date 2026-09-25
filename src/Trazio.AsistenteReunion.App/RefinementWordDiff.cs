using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Trazio.AsistenteReunion.App;

internal enum RefinementSensitiveChangeKind
{
    None,
    Negation,
    Date,
    Number,
    PossibleName
}

internal enum RefinementDiffSide
{
    Original,
    Proposal
}

internal sealed record RefinementDiffToken(
    string Text,
    bool IsChanged,
    RefinementSensitiveChangeKind SensitiveKind = RefinementSensitiveChangeKind.None,
    RefinementDiffSide Side = RefinementDiffSide.Original)
{
    public bool IsSensitive => SensitiveKind != RefinementSensitiveChangeKind.None;
    public string AccessibleDescription
    {
        get
        {
            var side = Side == RefinementDiffSide.Original ? "Original" : "Propuesta";
            var change = !IsChanged ? "sin cambios" :
                Side == RefinementDiffSide.Original ? "eliminado o sustituido" : "añadido o distinto";
            var sensitivity = SensitiveKind switch
            {
                RefinementSensitiveChangeKind.Negation => "; posible cambio de negación; verificar con audio",
                RefinementSensitiveChangeKind.Date => "; posible cambio de fecha; verificar con audio",
                RefinementSensitiveChangeKind.Number => "; posible cambio de cifra; verificar con audio",
                RefinementSensitiveChangeKind.PossibleName => "; posible cambio de nombre o sigla; verificar con audio",
                _ => string.Empty
            };
            return $"{side}: {Text.Trim()}; {change}{sensitivity}";
        }
    }
}

internal sealed record RefinementWordDiff(
    IReadOnlyList<RefinementDiffToken> Original,
    IReadOnlyList<RefinementDiffToken> Proposal)
{
    private static readonly Regex WordPattern = new(@"\S+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex DatePattern = new(@"^(?:\d{1,2}[/.-]\d{1,2}(?:[/.-]\d{2,4})?|\d{4}[/.-]\d{1,2}[/.-]\d{1,2})$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CurrencyAmountPattern = new(@"^(?:(?:CLP|USD|EUR|US)\$|[$€£])\s*[+-]?\d+(?:[.,]\d+)*$", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex NumberPattern = new(@"^[+-]?\d+(?:[.,]\d+)*(?:%|°)?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> Negations = new(StringComparer.Ordinal)
    {
        "no", "nunca", "jamas", "sin", "ningun", "ninguna", "ningunos", "ningunas", "nadie", "tampoco", "ni"
    };
    private static readonly HashSet<string> CommonCapitalizedWords = new(StringComparer.Ordinal)
    {
        "si", "el", "la", "los", "las", "un", "una", "en", "de", "del", "al", "es", "hoy", "ayer", "manana", "pero", "porque", "que", "clp", "usd", "eur"
    };
    private const int MaximumMatrixWords = 256;

    public bool HasSensitiveChanges => Original.Concat(Proposal).Any(token => token.IsSensitive);

    public string SensitiveChangeSummary
    {
        get
        {
            var kinds = Original.Concat(Proposal).Where(token => token.IsSensitive)
                .Select(token => token.SensitiveKind).Distinct().ToHashSet();
            if (kinds.Count == 0) return "Sin alertas heurísticas de nombres, cifras, fechas o negaciones. Escucha el audio igualmente.";
            var labels = new List<string>();
            if (kinds.Contains(RefinementSensitiveChangeKind.Negation)) labels.Add("negaciones");
            if (kinds.Contains(RefinementSensitiveChangeKind.Date)) labels.Add("fechas");
            if (kinds.Contains(RefinementSensitiveChangeKind.Number)) labels.Add("cifras");
            if (kinds.Contains(RefinementSensitiveChangeKind.PossibleName)) labels.Add("nombres o siglas");
            return $"Revisa con el audio posibles cambios en {string.Join(", ", labels)}. Es una alerta heurística, no una comprobación del significado.";
        }
    }

    public static RefinementWordDiff Compare(string original, string proposal)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(proposal);
        var left = WordPattern.Matches(original).Select(match => match.Value).ToArray();
        var right = WordPattern.Matches(proposal).Select(match => match.Value).ToArray();
        var leftChanged = Enumerable.Repeat(true, left.Length).ToArray();
        var rightChanged = Enumerable.Repeat(true, right.Length).ToArray();
        if (left.Length <= MaximumMatrixWords && right.Length <= MaximumMatrixWords)
        {
            var common = new int[left.Length + 1, right.Length + 1];
            for (var i = left.Length - 1; i >= 0; i--)
                for (var j = right.Length - 1; j >= 0; j--)
                    common[i, j] = string.Equals(left[i], right[j], StringComparison.Ordinal)
                        ? 1 + common[i + 1, j + 1]
                        : Math.Max(common[i + 1, j], common[i, j + 1]);
            for (int i = 0, j = 0; i < left.Length && j < right.Length;)
            {
                if (string.Equals(left[i], right[j], StringComparison.Ordinal))
                {
                    leftChanged[i++] = false;
                    rightChanged[j++] = false;
                }
                else if (common[i + 1, j] >= common[i, j + 1]) i++;
                else j++;
            }
        }
        else
        {
            var prefix = 0;
            while (prefix < Math.Min(left.Length, right.Length) &&
                   string.Equals(left[prefix], right[prefix], StringComparison.Ordinal))
                leftChanged[prefix] = rightChanged[prefix++] = false;
            var suffix = 0;
            while (suffix < Math.Min(left.Length, right.Length) - prefix &&
                   string.Equals(left[left.Length - suffix - 1], right[right.Length - suffix - 1], StringComparison.Ordinal))
            {
                leftChanged[left.Length - suffix - 1] = false;
                rightChanged[right.Length - suffix - 1] = false;
                suffix++;
            }
        }
        var changedOnLeft = left.Where((_, index) => leftChanged[index]).Select(NormalizeComparableToken)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changedOnRight = right.Where((_, index) => rightChanged[index]).Select(NormalizeComparableToken)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new(
            left.Select((text, index) => CreateToken(text, leftChanged[index],
                changedOnRight.Contains(NormalizeComparableToken(text)), RefinementDiffSide.Original)).ToArray(),
            right.Select((text, index) => CreateToken(text, rightChanged[index],
                changedOnLeft.Contains(NormalizeComparableToken(text)), RefinementDiffSide.Proposal)).ToArray());
    }

    private static RefinementDiffToken CreateToken(string text, bool changed, bool punctuationOrCaseOnly, RefinementDiffSide side) =>
        new(text + " ", changed, changed && !punctuationOrCaseOnly ? Classify(text) : RefinementSensitiveChangeKind.None, side);

    private static string NormalizeComparableToken(string text) =>
        text.Trim().Trim('"', '\'', '“', '”', '«', '»', '¿', '?', '¡', '!', '(', ')', '[', ']', '{', '}', ':', ';')
            .TrimEnd('.', ',');

    private static RefinementSensitiveChangeKind Classify(string text)
    {
        var value = NormalizeComparableToken(text);
        if (value.Length == 0) return RefinementSensitiveChangeKind.None;
        var normalized = RemoveDiacritics(value).ToLowerInvariant();
        if (Negations.Contains(normalized)) return RefinementSensitiveChangeKind.Negation;
        if (DatePattern.IsMatch(value)) return RefinementSensitiveChangeKind.Date;
        if (NumberPattern.IsMatch(value) || CurrencyAmountPattern.IsMatch(value)) return RefinementSensitiveChangeKind.Number;
        if (value.Length >= 2 && !CommonCapitalizedWords.Contains(normalized) && char.IsLetter(value[0]) &&
            (value.All(c => !char.IsLetter(c) || char.IsUpper(c)) ||
             (char.IsUpper(value[0]) && value.Skip(1).Any(char.IsLower))))
            return RefinementSensitiveChangeKind.PossibleName;
        return RefinementSensitiveChangeKind.None;
    }

    private static string RemoveDiacritics(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
                builder.Append(character);
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
}

internal sealed record RefinementBatchItem(Trazio.AsistenteReunion.Core.TranscriptRefinementBatch Batch)
{
    public string Label => $"{Batch.CreatedAt:yyyy-MM-dd HH:mm} · {Batch.GeneratorIdentity} · {Batch.Proposals.Count} propuesta(s)";
}

internal sealed record RefinementProposalItem(Trazio.AsistenteReunion.Core.TranscriptRefinementProposal Proposal)
{
    public string Label => Proposal.Ordinal == 0 ? "Propuesta conservadora" : "Alternativa por ambigüedad";
    public string Status => Proposal.ReviewStatus switch
    {
        Trazio.AsistenteReunion.Core.RefinementReviewStatus.Accepted => "Aceptada por una persona",
        Trazio.AsistenteReunion.Core.RefinementReviewStatus.Rejected => "Rechazada",
        _ => "Pendiente de revisión"
    };
    public string Text => Proposal.Text;
}
