using Trazio.AsistenteReunion.App;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.Tests;

public sealed class TranscriptPresentationTests
{
    [Fact]
    public void Format_WithNamedMicrophoneAndSystemAudio_UsesDistinctLabels()
    {
        var segments = new[]
        {
            Segment(AudioSourceKind.Microphone, "local", "Andrea"),
            Segment(AudioSourceKind.SystemOutput, "remote", null)
        };

        var result = TranscriptPresentation.Format(segments);

        Assert.Contains("] Andrea", result);
        Assert.Contains("] Audio del equipo", result);
        Assert.DoesNotContain("] Andrea" + Environment.NewLine + "remote", result);
    }

    [Fact]
    public void Format_OldMicrophoneRecordWithoutSpeaker_UsesLegacySourceLabel()
    {
        var result = TranscriptPresentation.Format([Segment(AudioSourceKind.Microphone, "legacy", null)]);

        Assert.Contains("] Micrófono", result);
    }

    private static TranscriptSegment Segment(AudioSourceKind source, string text, string? speaker) =>
        new(Guid.NewGuid().ToString("N"), "session", source, 0, TimeSpan.Zero,
            TimeSpan.FromSeconds(1), text, DateTimeOffset.UtcNow, speaker);

    [Fact]
    public void FormatReviewed_UsesCorrectionWhilePreservingMicrophoneAndSystemLabels()
    {
        var microphone = Segment(AudioSourceKind.Microphone, "raw mic", "Andrea");
        var system = Segment(AudioSourceKind.SystemOutput, "raw remote", null);
        var correction = new TranscriptCorrection("correction", microphone.Id, microphone.SessionId, 1,
            CorrectionAction.SetText, "corrected mic", "Andrea", DateTimeOffset.UtcNow);

        var result = TranscriptPresentation.FormatReviewed([
            new ReviewedTranscriptSegment(microphone, correction),
            new ReviewedTranscriptSegment(system, null)]);

        Assert.Contains("] Andrea" + Environment.NewLine + "corrected mic", result);
        Assert.Contains("] Audio del equipo" + Environment.NewLine + "raw remote", result);
        Assert.DoesNotContain("raw mic", result);
    }
    [Fact]
    public void HistorySegmentItem_ModelRevision_LabelsModelOutputWithoutHumanCorrection()
    {
        var segment = new TranscriptSegment("model-segment", "session", AudioSourceKind.Microphone, 0, TimeSpan.Zero, TimeSpan.FromSeconds(1), "generated", DateTimeOffset.UtcNow);
        var item = new HistorySegmentItem(new ReviewedTranscriptSegment(segment, null), "Model-generated revision · human corrections not applied");

        Assert.Equal("Model-generated revision · human corrections not applied", item.RevisionLabel);
        Assert.Equal("generated", item.Text);
    }
}

