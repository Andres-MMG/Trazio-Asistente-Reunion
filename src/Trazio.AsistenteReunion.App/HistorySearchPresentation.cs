using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.App;

public sealed record HistorySearchResultItem(
    HistorySearchHit Hit,
    string Title,
    string Details,
    string Snippet,
    string AutomationName)
{
    public static HistorySearchResultItem From(HistorySearchHit hit, TimeZoneInfo? timeZone = null)
    {
        timeZone ??= TimeZoneInfo.Local;
        var localStart = TimeZoneInfo.ConvertTime(hit.SessionStartedAt, timeZone);
        var location = hit.MatchKind == HistorySearchMatchKind.SessionTitle
            ? "Título de la reunión · Sin fuente · --:--"
            : $"{SourceName(hit.Source!.Value)} · {FormatOffset(hit.Start!.Value)}";
        var details = $"{localStart:yyyy-MM-dd HH:mm} · {location}";
        return new(
            hit,
            hit.SessionTitle,
            details,
            hit.Snippet,
            $"{hit.SessionTitle}. {details}. {hit.Snippet}");
    }

    private static string SourceName(AudioSourceKind source) => source == AudioSourceKind.Microphone
        ? "Micrófono"
        : "Audio del equipo";

    private static string FormatOffset(TimeSpan value) => value.TotalHours >= 1
        ? value.ToString("hh\\:mm\\:ss")
        : value.ToString("mm\\:ss");
}

public sealed record HistorySearchPresentationState(
    IReadOnlyList<HistorySearchResultItem> Items,
    string Status,
    bool IsTruncated);

public static class HistorySearchPresenter
{
    public const string IdleStatus = "Busca por el título de la reunión o por el texto revisado de la transcripción.";

    public static HistorySearchPresentationState Create(
        HistorySearchResult result,
        TimeZoneInfo? timeZone = null)
    {
        var items = result.Hits.Select(hit => HistorySearchResultItem.From(hit, timeZone)).ToArray();
        var status = result.IsTruncated
            ? $"Se muestran los primeros {items.Length} de {result.MatchCount} resultados. Acota la búsqueda para ver menos coincidencias."
            : result.MatchCount == 0
                ? "No se encontraron coincidencias."
                : result.MatchCount == 1
                    ? "Se encontró 1 resultado."
                    : $"Se encontraron {result.MatchCount} resultados.";
        return new(items, status, result.IsTruncated);
    }
}

public sealed record HistorySearchNavigationIntent(
    string SessionId,
    string? SegmentId,
    AudioSourceKind? Source,
    bool UseOriginalRevision,
    bool AutoPlay)
{
    public static HistorySearchNavigationIntent From(HistorySearchHit hit) =>
        new(hit.SessionId, hit.SegmentId, hit.Source, UseOriginalRevision: true, AutoPlay: false);
}
