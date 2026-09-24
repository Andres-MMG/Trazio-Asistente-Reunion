using System.ComponentModel;
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
public sealed class HistorySegmentItem : INotifyPropertyChanged
{
    private AnonymousVisualEvidenceViewModel _visualEvidence;
    private bool _isPlaybackActive;

    public HistorySegmentItem(
        ReviewedTranscriptSegment review,
        string? modelRevisionLabel = null,
        AnonymousVisualEvidenceViewModel? visualEvidence = null,
        bool reviewEligible = true)
    {
        Review = review ?? throw new ArgumentNullException(nameof(review));
        ModelRevisionLabel = modelRevisionLabel;
        ReviewEligible = reviewEligible;
        _visualEvidence = visualEvidence ??
            (review.Segment.Source == AudioSourceKind.Microphone
                ? AnonymousVisualEvidenceViewModel.Hidden
                : AnonymousVisualEvidenceViewModel.Unavailable);
    }

    public ReviewedTranscriptSegment Review { get; }
    public string? ModelRevisionLabel { get; }
    public bool ReviewEligible { get; }
    public TranscriptSegment Segment => Review.Segment;
    public string Header => $"{Segment.Start:hh\\:mm\\:ss} · {TranscriptPresentation.SpeakerLabel(Segment)}";
    public string Text => Review.EffectiveText;
    public string RevisionLabel => ModelRevisionLabel ?? Review.LatestRevision switch
    {
        { Action: CorrectionAction.SetText } correction => $"Corregida · versión {correction.Revision}",
        { Action: CorrectionAction.Undo } => "Revisada · original restaurado",
        null when Review.IsOriginalApproved => "Revisada sin cambios",
        _ => ReviewEligible ? "Pendiente de revisión" : "Transcripción original"
    };
    public AnonymousVisualEvidenceViewModel VisualEvidence => _visualEvidence;
    public bool IsPlaybackActive => _isPlaybackActive;
    public string PlaybackAnnouncement => _isPlaybackActive
        ? $"Reproduciendo este fragmento: {Header}"
        : string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetVisualEvidence(AnonymousVisualEvidenceViewModel visualEvidence)
    {
        ArgumentNullException.ThrowIfNull(visualEvidence);
        if (ReferenceEquals(_visualEvidence, visualEvidence) || _visualEvidence == visualEvidence) return;
        _visualEvidence = visualEvidence;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VisualEvidence)));
    }

    public void SetPlaybackActive(bool active)
    {
        if (_isPlaybackActive == active) return;
        _isPlaybackActive = active;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPlaybackActive)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PlaybackAnnouncement)));
    }
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

