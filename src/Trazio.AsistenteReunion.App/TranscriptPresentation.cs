using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.App;

public static class TranscriptPresentation
{
    public static string SourceName(AudioSourceKind source) =>
        source == AudioSourceKind.Microphone ? "Micrófono" : "Audio del equipo";

    public static string SpeakerLabel(TranscriptSegment segment) =>
        segment.Source == AudioSourceKind.Microphone && !string.IsNullOrWhiteSpace(segment.SpeakerName)
            ? segment.SpeakerName
            : SourceName(segment.Source);

    public static string FormatReviewed(IEnumerable<ReviewedTranscriptSegment> segments) =>
        string.Join(Environment.NewLine + Environment.NewLine,
            segments.Select(review =>
                $"[{review.Segment.Start:hh\\:mm\\:ss}] {SpeakerLabel(review.Segment)}{Environment.NewLine}{review.EffectiveText}"));
    public static string Format(IEnumerable<TranscriptSegment> segments) =>
        string.Join(Environment.NewLine + Environment.NewLine,
            segments.Select(segment =>
                $"[{segment.Start:hh\\:mm\\:ss}] {SpeakerLabel(segment)}{Environment.NewLine}{segment.Text}"));
}
public sealed record HistorySegmentItem(ReviewedTranscriptSegment Review, string? ModelRevisionLabel = null)
{
    public TranscriptSegment Segment => Review.Segment;
    public string Header => $"{Segment.Start:hh\\:mm\\:ss} · {TranscriptPresentation.SpeakerLabel(Segment)}";
    public string Text => Review.EffectiveText;
    public string RevisionLabel => ModelRevisionLabel ?? (Review.IsCorrected ? $"Corregida · versión {Review.LatestRevision!.Revision}" : "Transcripción original");
}
public static class TranscriptExport
{
    public static string CreatePlainText(IEnumerable<ReviewedTranscriptSegment> segments) =>
        TranscriptPresentation.FormatReviewed(segments);
}
public sealed record HistoryRevisionItem(TranscriptModelRevision Revision)
{
    public string Label => $"Versión del modelo · {Revision.StartedAt:yyyy-MM-dd HH:mm} · {Revision.ModelIdentity}";
    public override string ToString() => Label;
}

