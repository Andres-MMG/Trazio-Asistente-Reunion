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

public sealed record GlossaryEntry(
    string Id,
    string PreferredTerm,
    string MistakenForm,
    string Category,
    bool IsActive,
    string SourceCorrectionId,
    DateTimeOffset CreatedAt);