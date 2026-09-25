namespace Trazio.AsistenteReunion.Core;

public enum SegmentAnnotationKind
{
    Note,
    Decision,
    FollowUp
}

public enum SegmentAnnotationStatus
{
    Open,
    Completed
}

public enum SegmentAnnotationWriteStatus
{
    Applied,
    AlreadyCurrent,
    StateChanged,
    Missing
}

public static class SegmentAnnotationLimits
{
    public const int MaximumTextLength = 2_000;
    public const int MaximumAnnotationsPerSession = 200;

    public static string NormalizeText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();
        if (normalized.Length == 0)
            throw new ArgumentException("La anotación no puede estar vacía.", nameof(text));
        if (normalized.Length > MaximumTextLength)
            throw new ArgumentOutOfRangeException(nameof(text), $"La anotación admite hasta {MaximumTextLength:N0} caracteres.");
        if (normalized.Any(character => char.IsControl(character) && character is not ('\n' or '\t')))
            throw new ArgumentException("La anotación contiene caracteres de control no permitidos.", nameof(text));
        return normalized;
    }
}

public sealed record SegmentAnnotation
{
    public SegmentAnnotation(
        string id,
        string segmentId,
        string sessionId,
        SegmentAnnotationKind kind,
        string text,
        SegmentAnnotationStatus status,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(segmentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (!Enum.IsDefined(status)) throw new ArgumentOutOfRangeException(nameof(status));
        if (kind is not SegmentAnnotationKind.FollowUp && status is SegmentAnnotationStatus.Completed)
            throw new ArgumentException("Solo un seguimiento puede marcarse como completado.", nameof(status));

        Id = id;
        SegmentId = segmentId;
        SessionId = sessionId;
        Kind = kind;
        Text = SegmentAnnotationLimits.NormalizeText(text);
        Status = status;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    public string Id { get; init; }
    public string SegmentId { get; init; }
    public string SessionId { get; init; }
    public SegmentAnnotationKind Kind { get; init; }
    public string Text { get; init; }
    public SegmentAnnotationStatus Status { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}
