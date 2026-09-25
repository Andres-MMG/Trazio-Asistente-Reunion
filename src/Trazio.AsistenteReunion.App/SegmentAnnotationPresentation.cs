using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.App;

public sealed record SegmentAnnotationItem(
    SegmentAnnotation Annotation,
    string KindLabel,
    string StatusLabel,
    string TimestampLabel,
    bool CanToggleStatus,
    string ToggleStatusLabel,
    string AutomationName)
{
    public string Text => Annotation.Text;

    public static SegmentAnnotationItem From(SegmentAnnotation annotation, TimeZoneInfo? timeZone = null)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        timeZone ??= TimeZoneInfo.Local;
        var kind = annotation.Kind switch
        {
            SegmentAnnotationKind.Note => "Nota",
            SegmentAnnotationKind.Decision => "Decisión",
            SegmentAnnotationKind.FollowUp => "Seguimiento",
            _ => throw new ArgumentOutOfRangeException(nameof(annotation))
        };
        var status = annotation.Kind is SegmentAnnotationKind.FollowUp
            ? annotation.Status is SegmentAnnotationStatus.Completed ? "Completado" : "Pendiente"
            : "Guardada";
        var toggle = annotation.Status is SegmentAnnotationStatus.Completed ? "Reabrir" : "Completar";
        var local = TimeZoneInfo.ConvertTime(annotation.UpdatedAt, timeZone);
        return new(
            annotation,
            kind,
            status,
            $"{local:yyyy-MM-dd HH:mm}",
            annotation.Kind is SegmentAnnotationKind.FollowUp,
            toggle,
            $"{kind}. {status}. {annotation.Text}");
    }
}

public sealed record SegmentAnnotationViewState(
    IReadOnlyList<SegmentAnnotationItem> Items,
    bool IsTruncated,
    string Status);

public static class SegmentAnnotationPresenter
{
    public const int MaximumVisibleItems = 50;

    public static SegmentAnnotationViewState Create(
        IReadOnlyList<SegmentAnnotation> annotations,
        TimeZoneInfo? timeZone = null)
    {
        ArgumentNullException.ThrowIfNull(annotations);
        var ordered = annotations
            .OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.Id, StringComparer.Ordinal)
            .ToArray();
        var items = ordered
            .Take(MaximumVisibleItems)
            .Select(item => SegmentAnnotationItem.From(item, timeZone))
            .ToArray();
        var status = ordered.Length == 0
            ? "Este segmento todavía no tiene anotaciones."
            : IsTruncated(ordered.Length)
                ? $"Se muestran {MaximumVisibleItems} de {ordered.Length} anotaciones."
                : Summary(ordered);
        return new(items, IsTruncated(ordered.Length), status);
    }

    public static string Summary(IReadOnlyList<SegmentAnnotation> annotations)
    {
        ArgumentNullException.ThrowIfNull(annotations);
        if (annotations.Count == 0) return string.Empty;
        var openFollowUps = annotations.Count(item =>
            item.Kind is SegmentAnnotationKind.FollowUp &&
            item.Status is SegmentAnnotationStatus.Open);
        var annotationLabel = annotations.Count == 1 ? "1 anotación" : $"{annotations.Count} anotaciones";
        return openFollowUps == 0
            ? annotationLabel
            : $"{annotationLabel} · {openFollowUps} seguimiento{(openFollowUps == 1 ? string.Empty : "s")} pendiente{(openFollowUps == 1 ? string.Empty : "s")}";
    }

    private static bool IsTruncated(int count) => count > MaximumVisibleItems;
}
