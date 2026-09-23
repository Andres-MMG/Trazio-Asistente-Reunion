using System.Globalization;
using System.IO;
using System.Text;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.App;

public sealed record MarkdownExportDialogSettings(
    string FileName,
    string DefaultExtension,
    string Filter,
    bool AddExtension,
    bool OverwritePrompt);

public static class ObsidianMarkdownExport
{
    private static readonly HashSet<string> ReservedWindowsFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public const string PlaintextSyncWarning =
        "La nota Markdown quedará sin cifrar. Si eliges una bóveda de Obsidian sincronizada, su contenido podría enviarse a servicios externos. No se exportarán audio, rutas ni identificadores internos. ¿Deseas continuar?";

    public static Encoding Utf8WithoutBom { get; } = new UTF8Encoding(false);

    public static MarkdownExportDialogSettings DialogSettings(string sessionTitle) => new(
        SuggestedFileName(sessionTitle),
        ".md",
        "Nota Markdown (*.md)|*.md",
        AddExtension: true,
        OverwritePrompt: true);

    public static string Create(
        SessionSummary session,
        IEnumerable<ReviewedTranscriptSegment> segments,
        string applicationVersion)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(segments);

        var version = string.IsNullOrWhiteSpace(applicationVersion) ? "desconocida" : applicationVersion.Trim();
        var builder = new StringBuilder();
        builder.AppendLine("---");
        builder.Append("title: ").AppendLine(YamlString(session.Title));
        builder.Append("date: ").AppendLine(YamlString(session.StartedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        builder.Append("started_at: ").AppendLine(YamlString(session.StartedAt.ToString("O", CultureInfo.InvariantCulture)));
        builder.Append("ended_at: ").AppendLine(session.EndedAt is null
            ? "null"
            : YamlString(session.EndedAt.Value.ToString("O", CultureInfo.InvariantCulture)));
        builder.Append("status: ").AppendLine(SessionStatus(session.State));
        builder.AppendLine("type: meeting-transcript");
        builder.AppendLine("generator: trazio-asistente-reunion");
        builder.Append("generator_version: ").AppendLine(YamlString(version));
        builder.AppendLine("tags:");
        builder.AppendLine("  - trazio");
        builder.AppendLine("  - reunion");
        builder.AppendLine("  - transcripcion");
        builder.AppendLine("---");
        builder.AppendLine();
        builder.Append("# ").AppendLine(MarkdownInline(session.Title));
        builder.AppendLine();
        builder.AppendLine("## Transcripción");

        foreach (var review in segments
                     .OrderBy(item => item.Segment.Start)
                     .ThenBy(item => item.Segment.Sequence)
                     .ThenBy(item => item.Segment.Source))
        {
            var segment = review.Segment;
            builder.AppendLine();
            builder.Append("### ")
                .Append(segment.Start.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture))
                .Append(" · ")
                .AppendLine(MarkdownInline(TranscriptPresentation.SpeakerLabel(segment)));
            builder.Append("**Fuente:** ")
                .AppendLine(MarkdownInline(TranscriptPresentation.SourceName(segment.Source)));
            builder.AppendLine();
            builder.AppendLine(MarkdownBody(review.EffectiveText));
        }

        return builder.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    public static string SuggestedFileName(string title)
    {
        var safe = string.Concat((title ?? string.Empty)
            .Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character))
            .Trim()
            .TrimEnd('.');
        if (string.IsNullOrWhiteSpace(safe)) safe = "reunion";

        var deviceStem = safe.Split('.', 2)[0];
        if (ReservedWindowsFileNames.Contains(deviceStem)) safe = "_" + safe;
        return safe + ".md";
    }

    private static string SessionStatus(SessionState state) => state switch
    {
        SessionState.Recording => "recording",
        SessionState.Paused => "paused",
        SessionState.Completed => "completed",
        SessionState.Interrupted => "interrupted",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
    };

    private static string YamlString(string value)
    {
        var builder = new StringBuilder(value.Length + 2).Append('"');
        foreach (var character in NormalizeLineEndings(value))
        {
            switch (character)
            {
                case '\\': builder.Append("\\\\"); break;
                case '"': builder.Append("\\\""); break;
                case '\n': builder.Append("\\n"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (char.IsControl(character)) builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    else builder.Append(character);
                    break;
            }
        }
        return builder.Append('"').ToString();
    }

    private static string MarkdownInline(string value) =>
        MarkdownBody(value).Replace("\n", " ", StringComparison.Ordinal);

    private static string MarkdownBody(string value)
    {
        var escaped = EscapeHtml(NormalizeLineEndings(value))
            .Replace("[", "&#91;", StringComparison.Ordinal)
            .Replace("]", "&#93;", StringComparison.Ordinal);
        return string.Join("\n", escaped.Split('\n').Select(EscapeMarkdownLineStart));
    }

    private static string EscapeHtml(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal);

    private static string EscapeMarkdownLineStart(string line)
    {
        var index = 0;
        while (index < line.Length && char.IsWhiteSpace(line[index])) index++;
        if (index >= line.Length) return line;

        if (line[index] is '#' or '>' or '-' or '+' or '*' or '`')
            return line.Insert(index, "\\");

        var digitEnd = index;
        while (digitEnd < line.Length && char.IsDigit(line[digitEnd])) digitEnd++;
        return digitEnd > index && digitEnd < line.Length && line[digitEnd] == '.'
            ? line.Insert(digitEnd, "\\")
            : line;
    }

    private static string NormalizeLineEndings(string value) =>
        (value ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}
