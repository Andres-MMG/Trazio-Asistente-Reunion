namespace Trazio.AsistenteReunion.Core;

public enum CorrectionAction { SetText, Undo }

public sealed record TranscriptCorrection(
    string Id,
    string SegmentId,
    string SessionId,
    int Revision,
    CorrectionAction Action,
    string? CorrectedText,
    string? EditorName,
    DateTimeOffset CreatedAt);

public sealed record ReviewedTranscriptSegment(
    TranscriptSegment Segment,
    TranscriptCorrection? LatestRevision)
{
    public string EffectiveText => LatestRevision is { Action: CorrectionAction.SetText, CorrectedText: not null }
        ? LatestRevision.CorrectedText
        : Segment.Text;
    public bool IsCorrected => LatestRevision?.Action == CorrectionAction.SetText;
}

public enum GlossaryEntryOrigin
{
    TranscriptCorrection,
    ImportedFile
}

public sealed record GlossaryEntry
{
    public GlossaryEntry(
        string id,
        string preferredTerm,
        string mistakenForm,
        string category,
        bool isActive,
        GlossaryEntryOrigin origin,
        string? sourceCorrectionId,
        string? importBatchId,
        DateTimeOffset createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(preferredTerm);
        ArgumentException.ThrowIfNullOrWhiteSpace(mistakenForm);
        ArgumentException.ThrowIfNullOrWhiteSpace(category);

        var hasCorrection = !string.IsNullOrWhiteSpace(sourceCorrectionId);
        var hasImportBatch = !string.IsNullOrWhiteSpace(importBatchId);
        if (origin == GlossaryEntryOrigin.TranscriptCorrection && (!hasCorrection || hasImportBatch))
            throw new ArgumentException("Una entrada proveniente de una corrección debe tener solo esa procedencia.");
        if (origin == GlossaryEntryOrigin.ImportedFile && (hasCorrection || !hasImportBatch))
            throw new ArgumentException("Una entrada importada debe tener solo un lote de importación.");

        Id = id;
        PreferredTerm = preferredTerm;
        MistakenForm = mistakenForm;
        Category = category;
        IsActive = isActive;
        Origin = origin;
        SourceCorrectionId = sourceCorrectionId;
        ImportBatchId = importBatchId;
        CreatedAt = createdAt;
    }

    public string Id { get; init; }
    public string PreferredTerm { get; init; }
    public string MistakenForm { get; init; }
    public string Category { get; init; }
    public bool IsActive { get; init; }
    public GlossaryEntryOrigin Origin { get; init; }
    public string? SourceCorrectionId { get; init; }
    public string? ImportBatchId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
