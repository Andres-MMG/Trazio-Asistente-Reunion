using System.IO;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.App;

public sealed record GlossaryImportPreviewRow(
    int RowNumber,
    string ReplacementLabel,
    string CategoryLabel,
    string ActivityLabel,
    string ClassificationLabel,
    string Reason,
    string AutomationName);

public sealed record GlossaryImportPreviewPresentation(
    IReadOnlyList<GlossaryImportPreviewRow> Rows,
    int NewCount,
    string CountsLabel,
    string ImportButtonLabel);

public static class GlossaryImportPreviewPresenter
{
    public static GlossaryImportPreviewPresentation Create(GlossaryImportPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        var rows = preview.Items.Select(CreateRow).ToArray();
        var counts = $"{preview.NewCount} nuevas · {preview.ExactDuplicateCount} duplicadas exactas · " +
                     $"{preview.NormalizedDuplicateCount} duplicadas normalizadas · {preview.ConflictCount} conflictos · " +
                     $"{preview.RejectedCount} rechazadas";
        return new(rows, preview.NewCount, counts, $"Importar {preview.NewCount} nuevas");
    }

    private static GlossaryImportPreviewRow CreateRow(GlossaryImportPreviewItem item)
    {
        var replacement = item.Entry is null
            ? "Fila sin datos utilizables"
            : $"{item.Entry.MistakenForm} → {item.Entry.PreferredTerm}";
        var category = item.Entry?.Category ?? "—";
        var activity = item.Entry is null ? "—" : item.Entry.IsActive ? "Activa" : "Inactiva";
        var classification = item.Classification switch
        {
            GlossaryImportClassification.New => "Nueva",
            GlossaryImportClassification.ExactDuplicate => "Duplicada exacta",
            GlossaryImportClassification.NormalizedDuplicate => "Duplicada normalizada",
            GlossaryImportClassification.Conflict => "Conflicto",
            _ => "Rechazada"
        };
        return new(
            item.RowNumber,
            replacement,
            category,
            activity,
            classification,
            item.Reason,
            $"Fila {item.RowNumber}. {replacement}. Categoría {category}. Estado {activity}. Clasificación {classification}. Motivo: {item.Reason}");
    }
}

public static class GlossaryExchangeFileWriter
{
    public static async Task WriteAtomicallyAsync(
        string destinationPath,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var fullPath = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("No se pudo determinar la carpeta de destino.");
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException("La carpeta de destino ya no existe.");

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             81_920,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(content, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(fullPath)) File.Replace(temporaryPath, fullPath, null, ignoreMetadataErrors: true);
            else File.Move(temporaryPath, fullPath);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch { }
        }
    }
}
