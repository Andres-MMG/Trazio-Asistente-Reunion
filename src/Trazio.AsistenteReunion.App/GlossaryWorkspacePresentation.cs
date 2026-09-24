using System.Globalization;
using System.Text;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.App;

public enum GlossaryEntryActivityFilter
{
    All,
    Active,
    Inactive
}

public sealed record GlossaryWorkspaceItem(
    int Ordinal,
    string MistakenForm,
    string PreferredTerm,
    string Category,
    bool IsActive,
    string CreatedAtLabel,
    string SourceLabel,
    string AutomationName)
{
    internal string EntryId { get; init; } = string.Empty;
    public string OrdinalLabel => $"#{Ordinal}";
    public string ReplacementLabel => $"{MistakenForm} → {PreferredTerm}";
}

public sealed record GlossaryWorkspacePresentationState(
    IReadOnlyList<GlossaryWorkspaceItem> Items,
    int TotalCount,
    int ActiveCount,
    int MatchingCount,
    bool IsTruncated,
    string Status);

public static class GlossaryWorkspacePresenter
{
    public const int MaximumVisibleItems = 120;
    public const string LoadingStatus = "Cargando diccionario…";
    public const string ErrorStatus = "No se pudo cargar el diccionario. No se muestran resultados parciales.";

    public static GlossaryWorkspacePresentationState Create(
        IReadOnlyList<GlossaryEntry> entries,
        string? filter,
        GlossaryEntryActivityFilter activityFilter,
        TimeZoneInfo? timeZone = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        timeZone ??= TimeZoneInfo.Local;

        var ordered = entries
            .OrderByDescending(entry => entry.CreatedAt)
            .ThenByDescending(entry => entry.Id, StringComparer.Ordinal)
            .Select((entry, index) => CreateItem(entry, index + 1, timeZone))
            .ToArray();
        var normalizedFilter = Normalize(filter);
        var matches = ordered
            .Where(item => MatchesActivity(item, activityFilter))
            .Where(item => MatchesText(item, normalizedFilter))
            .ToArray();
        var visible = matches.Take(MaximumVisibleItems).ToArray();
        var activeCount = ordered.Count(item => item.IsActive);
        var truncated = matches.Length > visible.Length;

        return new(
            visible,
            ordered.Length,
            activeCount,
            matches.Length,
            truncated,
            CreateStatus(visible.Length, matches.Length, ordered.Length, activeCount, truncated));
    }

    private static GlossaryWorkspaceItem CreateItem(GlossaryEntry entry, int ordinal, TimeZoneInfo timeZone)
    {
        var localCreatedAt = TimeZoneInfo.ConvertTime(entry.CreatedAt, timeZone);
        const string source = "Corrección de transcripción";
        var state = entry.IsActive ? "Activa" : "Inactiva";
        return new(
            ordinal,
            entry.MistakenForm,
            entry.PreferredTerm,
            entry.Category,
            entry.IsActive,
            localCreatedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            source,
            $"Entrada {ordinal}. {entry.MistakenForm}, reemplazar por {entry.PreferredTerm}. Categoría {entry.Category}. Fecha {localCreatedAt:yyyy-MM-dd HH:mm}. Origen {source}. Estado {state}.")
        {
            EntryId = entry.Id
        };
    }

    private static bool MatchesActivity(GlossaryWorkspaceItem item, GlossaryEntryActivityFilter filter) => filter switch
    {
        GlossaryEntryActivityFilter.Active => item.IsActive,
        GlossaryEntryActivityFilter.Inactive => !item.IsActive,
        _ => true
    };

    private static bool MatchesText(GlossaryWorkspaceItem item, string normalizedFilter)
    {
        if (normalizedFilter.Length == 0) return true;
        return Normalize(item.MistakenForm).Contains(normalizedFilter, StringComparison.Ordinal) ||
               Normalize(item.PreferredTerm).Contains(normalizedFilter, StringComparison.Ordinal) ||
               Normalize(item.Category).Contains(normalizedFilter, StringComparison.Ordinal);
    }

    private static string CreateStatus(
        int visibleCount,
        int matchingCount,
        int totalCount,
        int activeCount,
        bool truncated)
    {
        var visibleLabel = visibleCount == 1 ? "1 visible" : $"{visibleCount} visibles";
        var totalLabel = totalCount == 1 ? "1 entrada" : $"{totalCount} entradas";
        var activeLabel = activeCount == 1 ? "1 activa" : $"{activeCount} activas";
        var counts = $"{visibleLabel} de {totalLabel}; {activeLabel}.";
        if (totalCount == 0) return $"{counts} El diccionario todavía no tiene entradas.";
        if (matchingCount == 0) return $"{counts} No hay coincidencias para el filtro actual.";
        if (truncated)
            return $"{counts} Se muestran las primeras {MaximumVisibleItems} de {matchingCount} coincidencias; acota el filtro.";
        return counts;
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var decomposed = value.Trim().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
                builder.Append(char.ToUpperInvariant(character));
        }
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
}
