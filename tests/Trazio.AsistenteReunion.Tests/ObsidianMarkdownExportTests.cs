using Trazio.AsistenteReunion.App;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.Tests;

public sealed class ObsidianMarkdownExportTests
{
    [Fact]
    public void Create_WithReviewedSources_WritesSafeMetadataAndChronologicalEffectiveTranscript()
    {
        var started = new DateTimeOffset(2026, 9, 22, 10, 0, 0, TimeSpan.FromHours(-3));
        var session = new SessionSummary(
            "private-session-id",
            "Reunión \"crítica\"",
            started,
            started.AddMinutes(20),
            SessionState.Completed);
        var microphone = Segment(
            "private-microphone-id",
            AudioSourceKind.Microphone,
            sequence: 2,
            start: TimeSpan.FromSeconds(5),
            text: "texto original",
            speaker: "Andrea");
        var system = Segment(
            "private-system-id",
            AudioSourceKind.SystemOutput,
            sequence: 1,
            start: TimeSpan.FromSeconds(1),
            text: "respuesta remota",
            speaker: null);
        var correction = new TranscriptCorrection(
            "private-correction-id",
            microphone.Id,
            microphone.SessionId,
            1,
            CorrectionAction.SetText,
            "texto corregido",
            "Andrea",
            started.AddMinutes(1));

        var markdown = ObsidianMarkdownExport.Create(
            session,
            [new ReviewedTranscriptSegment(microphone, correction), new ReviewedTranscriptSegment(system, null)],
            "0.2.0-beta.1");

        Assert.StartsWith("---\n", markdown);
        Assert.Contains("title: \"Reunión \\\"crítica\\\"\"", markdown);
        Assert.Contains("date: \"2026-09-22\"", markdown);
        Assert.Contains("started_at: \"2026-09-22T10:00:00.0000000-03:00\"", markdown);
        Assert.Contains("ended_at: \"2026-09-22T10:20:00.0000000-03:00\"", markdown);
        Assert.Contains("status: completed", markdown);
        Assert.Contains("generator_version: \"0.2.0-beta.1\"", markdown);
        Assert.Contains("  - transcripcion", markdown);
        Assert.Contains("### 00:00:01 · Audio del equipo\n**Fuente:** Audio del equipo", markdown);
        Assert.Contains("### 00:00:05 · Andrea\n**Fuente:** Micrófono", markdown);
        Assert.True(markdown.IndexOf("00:00:01", StringComparison.Ordinal) < markdown.IndexOf("00:00:05", StringComparison.Ordinal));
        Assert.Contains("texto corregido", markdown);
        Assert.DoesNotContain("texto original", markdown);
        Assert.DoesNotContain("private-session-id", markdown);
        Assert.DoesNotContain("private-microphone-id", markdown);
        Assert.DoesNotContain("private-correction-id", markdown);
    }

    [Fact]
    public void Create_WithInterruptedSessionAndUnsafeMarkdown_UsesNullEndAndNeutralizesActiveContent()
    {
        var session = new SessionSummary(
            "session",
            "Título\nsegunda línea",
            new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero),
            null,
            SessionState.Interrupted);
        var segment = Segment(
            "segment",
            AudioSourceKind.Microphone,
            sequence: 0,
            start: TimeSpan.Zero,
            text: "<script>alert('á')</script>\r\n![[secreto.png]]\r\n[documento](https://example.test/doc)\r\n![](https://example.test/imagen.png)\r\n# encabezado\r\n1. lista",
            speaker: "![[persona]]");

        var markdown = ObsidianMarkdownExport.Create(
            session,
            [new ReviewedTranscriptSegment(segment, null)],
            "0.2.0-beta.1");

        Assert.Contains("title: \"Título\\nsegunda línea\"", markdown);
        Assert.Contains("ended_at: null", markdown);
        Assert.Contains("status: interrupted", markdown);
        Assert.Contains("&lt;script&gt;alert('á')&lt;/script&gt;", markdown);
        Assert.Contains("!&#91;&#91;secreto.png&#93;&#93;", markdown);
        Assert.Contains("!&#91;&#91;persona&#93;&#93;", markdown);
        Assert.Contains("&#91;documento&#93;(https://example.test/doc)", markdown);
        Assert.Contains("!&#91;&#93;(https://example.test/imagen.png)", markdown);
        Assert.Contains("\\# encabezado", markdown);
        Assert.Contains("1\\. lista", markdown);
        Assert.DoesNotContain("<script>", markdown);
        Assert.DoesNotContain("![[", markdown);
        Assert.DoesNotContain("[documento](", markdown);
        Assert.DoesNotContain("![](", markdown);
    }

    [Fact]
    public void DialogSettings_UsesSanitizedMarkdownNameAndSafeSaveBehavior()
    {
        var settings = ObsidianMarkdownExport.DialogSettings(" Reunión: seguimiento. ");

        Assert.Equal("Reunión_ seguimiento.md", settings.FileName);
        Assert.Equal(".md", settings.DefaultExtension);
        Assert.Contains("*.md", settings.Filter);
        Assert.True(settings.AddExtension);
        Assert.True(settings.OverwritePrompt);
        Assert.Empty(ObsidianMarkdownExport.Utf8WithoutBom.GetPreamble());
        Assert.Contains("sin cifrar", ObsidianMarkdownExport.PlaintextSyncWarning);
        Assert.Contains("bóveda de Obsidian sincronizada", ObsidianMarkdownExport.PlaintextSyncWarning);
        Assert.Contains("servicios externos", ObsidianMarkdownExport.PlaintextSyncWarning);
        Assert.Equal("_CON.md", ObsidianMarkdownExport.SuggestedFileName("CON"));
        Assert.Equal("_lpt9.notas.md", ObsidianMarkdownExport.SuggestedFileName("lpt9.notas"));
    }

    private static TranscriptSegment Segment(
        string id,
        AudioSourceKind source,
        long sequence,
        TimeSpan start,
        string text,
        string? speaker) =>
        new(id, "session", source, sequence, start, start.Add(TimeSpan.FromSeconds(3)), text, DateTimeOffset.UtcNow, speaker);
}
