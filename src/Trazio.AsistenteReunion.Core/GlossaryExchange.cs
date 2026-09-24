using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Trazio.AsistenteReunion.Core;

public sealed record GlossaryExchangeEntry(
    string MistakenForm,
    string PreferredTerm,
    string Category,
    bool IsActive);

public sealed record GlossaryImportRow(
    int RowNumber,
    GlossaryExchangeEntry? Entry,
    string? RejectionReason);

public sealed record GlossaryImportParseResult(IReadOnlyList<GlossaryImportRow> Rows);

public enum GlossaryImportClassification
{
    New,
    ExactDuplicate,
    NormalizedDuplicate,
    Conflict,
    Rejected
}

public sealed record GlossaryImportPreviewItem(
    int RowNumber,
    GlossaryExchangeEntry? Entry,
    GlossaryImportClassification Classification,
    string Reason);

public sealed record GlossaryImportPreview(
    IReadOnlyList<GlossaryImportPreviewItem> Items,
    string ExistingSnapshotToken)
{
    public int NewCount => Items.Count(item => item.Classification == GlossaryImportClassification.New);
    public int ExactDuplicateCount => Items.Count(item => item.Classification == GlossaryImportClassification.ExactDuplicate);
    public int NormalizedDuplicateCount => Items.Count(item => item.Classification == GlossaryImportClassification.NormalizedDuplicate);
    public int ConflictCount => Items.Count(item => item.Classification == GlossaryImportClassification.Conflict);
    public int RejectedCount => Items.Count(item => item.Classification == GlossaryImportClassification.Rejected);
    public IReadOnlyList<GlossaryExchangeEntry> NewEntries => Items
        .Where(item => item.Classification == GlossaryImportClassification.New && item.Entry is not null)
        .Select(item => item.Entry!)
        .ToArray();
}

public enum GlossaryExistingIssueKind
{
    ExactDuplicate,
    NormalizedDuplicate,
    Conflict
}

public sealed record GlossaryExistingIssue(
    GlossaryExistingIssueKind Kind,
    int GroupSize,
    string Reason);

public sealed class GlossaryImportSnapshotChangedException : InvalidOperationException
{
    public GlossaryImportSnapshotChangedException()
        : base("El diccionario cambió desde la vista previa. Vuelve a revisar el archivo antes de importarlo.") { }
}

public static class GlossaryExchangeSerializer
{
    public const string Format = "trazio-glossary";
    public const int SchemaVersion = 1;
    public const int MaximumFileBytes = 5 * 1024 * 1024;
    public const int MaximumEntries = 5_000;
    public const int MaximumTermLength = 120;
    public const int MaximumCategoryLength = 60;
    public const int MaximumJsonDepth = 8;

    private static readonly string[] RootProperties = ["format", "schemaVersion", "entries"];
    private static readonly string[] EntryProperties = ["mistakenForm", "preferredTerm", "category", "isActive"];

    public static byte[] Serialize(IReadOnlyList<GlossaryEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count > MaximumEntries)
            throw new InvalidDataException($"El diccionario supera el límite portable de {MaximumEntries} entradas; no se exportó un archivo parcial.");
        var portableEntries = entries.Select(entry => new
        {
            SourceId = entry.Id,
            Value = NormalizeAndValidate(new(
                entry.MistakenForm,
                entry.PreferredTerm,
                entry.Category,
                entry.IsActive))
        }).ToArray();
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("format", Format);
            writer.WriteNumber("schemaVersion", SchemaVersion);
            writer.WriteStartArray("entries");
            foreach (var item in portableEntries
                         .OrderBy(item => GlossaryExchangePlanner.NormalizeForComparison(item.Value.MistakenForm), StringComparer.Ordinal)
                         .ThenBy(item => item.Value.MistakenForm, StringComparer.Ordinal)
                         .ThenBy(item => GlossaryExchangePlanner.NormalizeForComparison(item.Value.PreferredTerm), StringComparer.Ordinal)
                         .ThenBy(item => item.Value.PreferredTerm, StringComparer.Ordinal)
                         .ThenBy(item => GlossaryExchangePlanner.NormalizeForComparison(item.Value.Category), StringComparer.Ordinal)
                         .ThenBy(item => item.Value.Category, StringComparer.Ordinal)
                         .ThenBy(item => item.Value.IsActive)
                         .ThenBy(item => item.SourceId, StringComparer.Ordinal))
            {
                var entry = item.Value;
                writer.WriteStartObject();
                writer.WriteString("mistakenForm", entry.MistakenForm);
                writer.WriteString("preferredTerm", entry.PreferredTerm);
                writer.WriteString("category", entry.Category);
                writer.WriteBoolean("isActive", entry.IsActive);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        var json = Encoding.UTF8.GetString(buffer.ToArray())
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        var bytes = Encoding.UTF8.GetBytes(json);
        if (bytes.Length > MaximumFileBytes)
            throw new InvalidDataException($"El diccionario supera el límite portable de {MaximumFileBytes} bytes; no se exportó un archivo parcial.");
        return bytes;
    }

    public static async Task<GlossaryImportParseResult> ParseAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead) throw new ArgumentException("El flujo del archivo no se puede leer.", nameof(stream));

        var bytes = await ReadBoundedAsync(stream, cancellationToken);
        var payload = bytes.AsMemory();
        if (payload.Length >= 3 && payload.Span[0] == 0xEF && payload.Span[1] == 0xBB && payload.Span[2] == 0xBF)
            payload = payload[3..];

        try
        {
            using var document = JsonDocument.Parse(payload, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = MaximumJsonDepth
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("El archivo debe contener un objeto JSON.");
            var rootError = ExactPropertiesError(root, RootProperties);
            if (rootError is not null) throw new InvalidDataException($"El documento no cumple JSON v1: {rootError}");
            if (root.GetProperty("format").ValueKind != JsonValueKind.String ||
                !string.Equals(root.GetProperty("format").GetString(), Format, StringComparison.Ordinal))
                throw new InvalidDataException($"El formato debe ser '{Format}'.");
            if (root.GetProperty("schemaVersion").ValueKind != JsonValueKind.Number ||
                !root.GetProperty("schemaVersion").TryGetInt32(out var version) ||
                version != SchemaVersion)
                throw new InvalidDataException($"Solo se admite schemaVersion {SchemaVersion}.");

            var entries = root.GetProperty("entries");
            if (entries.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("La propiedad entries debe ser una lista.");
            var count = entries.GetArrayLength();
            if (count > MaximumEntries)
                throw new InvalidDataException($"El archivo supera el límite de {MaximumEntries} filas.");

            var rows = new List<GlossaryImportRow>(count);
            var rowNumber = 0;
            foreach (var element in entries.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                rowNumber++;
                rows.Add(ParseRow(element, rowNumber));
            }
            return new(rows);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("El archivo no contiene JSON UTF-8 válido.", exception);
        }
    }

    public static GlossaryExchangeEntry NormalizeAndValidate(GlossaryExchangeEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new(
            ValidateAndNormalizeField(entry.MistakenForm, MaximumTermLength, "forma detectada"),
            ValidateAndNormalizeField(entry.PreferredTerm, MaximumTermLength, "término preferido"),
            ValidateAndNormalizeField(entry.Category, MaximumCategoryLength, "categoría"),
            entry.IsActive);
    }

    private static GlossaryImportRow ParseRow(JsonElement element, int rowNumber)
    {
        try
        {
            if (element.ValueKind != JsonValueKind.Object)
                return Rejected(rowNumber, "La fila debe ser un objeto.");
            var propertyError = ExactPropertiesError(element, EntryProperties);
            if (propertyError is not null) return Rejected(rowNumber, propertyError);
            if (element.GetProperty("mistakenForm").ValueKind != JsonValueKind.String ||
                element.GetProperty("preferredTerm").ValueKind != JsonValueKind.String ||
                element.GetProperty("category").ValueKind != JsonValueKind.String ||
                element.GetProperty("isActive").ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return Rejected(rowNumber, "Los tipos de datos de la fila no son válidos.");

            var entry = NormalizeAndValidate(new(
                element.GetProperty("mistakenForm").GetString()!,
                element.GetProperty("preferredTerm").GetString()!,
                element.GetProperty("category").GetString()!,
                element.GetProperty("isActive").GetBoolean()));
            return new(rowNumber, entry, null);
        }
        catch (ArgumentException exception)
        {
            return Rejected(rowNumber, exception.Message);
        }
    }

    private static GlossaryImportRow Rejected(int rowNumber, string reason) => new(rowNumber, null, reason);

    private static string ValidateAndNormalizeField(string? value, int maximumLength, string label)
    {
        if (value is null) throw new ArgumentException($"Falta {label}.");
        string normalized;
        try { normalized = value.Trim().Normalize(NormalizationForm.FormC); }
        catch (ArgumentException) { throw new ArgumentException($"El campo {label} contiene Unicode inválido."); }
        if (normalized.Length is < 1) throw new ArgumentException($"El campo {label} está vacío.");
        if (normalized.Length > maximumLength)
            throw new ArgumentException($"El campo {label} supera {maximumLength} caracteres.");
        if (normalized.Any(char.IsControl))
            throw new ArgumentException($"El campo {label} contiene caracteres de control.");
        return normalized;
    }

    private static string? ExactPropertiesError(JsonElement element, IReadOnlyCollection<string> expected)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name)) return $"La propiedad '{property.Name}' está repetida.";
            if (!expected.Contains(property.Name, StringComparer.Ordinal))
                return $"La propiedad '{property.Name}' no está permitida o usa mayúsculas incorrectas.";
        }
        foreach (var required in expected)
            if (!seen.Contains(required)) return $"Falta la propiedad '{required}'.";
        return null;
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(81_920);
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0) break;
                if (output.Length + read > MaximumFileBytes)
                    throw new InvalidDataException($"El archivo supera el límite de {MaximumFileBytes / (1024 * 1024)} MiB.");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            return output.ToArray();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
}

public static class GlossaryExchangePlanner
{
    public static GlossaryImportPreview CreatePreview(
        IReadOnlyList<GlossaryEntry> existing,
        GlossaryImportParseResult parsed)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(parsed);

        var validRows = parsed.Rows.Where(row => row.Entry is not null).ToArray();
        var preferredByMistaken = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var entry in existing.Select(ToPortable).Concat(validRows.Select(row => row.Entry!)))
        {
            var mistaken = NormalizeForComparison(entry.MistakenForm);
            if (!preferredByMistaken.TryGetValue(mistaken, out var preferred))
                preferredByMistaken[mistaken] = preferred = new(StringComparer.Ordinal);
            preferred.Add(NormalizeForComparison(entry.PreferredTerm));
        }

        var existingByPair = existing.GroupBy(entry => PairKey(ToPortable(entry))).ToDictionary(
            group => group.Key,
            group => group.Select(ToPortable).ToArray(),
            StringComparer.Ordinal);
        var seenByPair = new Dictionary<string, List<GlossaryExchangeEntry>>(StringComparer.Ordinal);
        var items = new List<GlossaryImportPreviewItem>(parsed.Rows.Count);
        foreach (var row in parsed.Rows.OrderBy(item => item.RowNumber))
        {
            if (row.Entry is null)
            {
                items.Add(new(row.RowNumber, null, GlossaryImportClassification.Rejected,
                    row.RejectionReason ?? "La fila no es válida."));
                continue;
            }

            var entry = row.Entry;
            var mistaken = NormalizeForComparison(entry.MistakenForm);
            var pair = PairKey(entry);
            if (preferredByMistaken[mistaken].Count > 1)
            {
                items.Add(new(row.RowNumber, entry, GlossaryImportClassification.Conflict,
                    "La misma forma detectada apunta a términos preferidos diferentes."));
                AddSeen(seenByPair, pair, entry);
                continue;
            }

            var comparable = existingByPair.GetValueOrDefault(pair, Array.Empty<GlossaryExchangeEntry>())
                .Concat(seenByPair.GetValueOrDefault(pair, []))
                .ToArray();
            if (comparable.Any(candidate => ExactSemanticEquals(candidate, entry)))
            {
                items.Add(new(row.RowNumber, entry, GlossaryImportClassification.ExactDuplicate,
                    "Ya existe una entrada exactamente igual."));
            }
            else if (comparable.Length > 0)
            {
                items.Add(new(row.RowNumber, entry, GlossaryImportClassification.NormalizedDuplicate,
                    "El mismo par ya existe con otra grafía, categoría o estado."));
            }
            else
            {
                items.Add(new(row.RowNumber, entry, GlossaryImportClassification.New,
                    "Entrada nueva lista para importar."));
            }
            AddSeen(seenByPair, pair, entry);
        }
        return new(items, ComputeSnapshotToken(existing));
    }

    public static IReadOnlyDictionary<string, GlossaryExistingIssue> AnalyzeExisting(
        IReadOnlyList<GlossaryEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var result = new Dictionary<string, GlossaryExistingIssue>(StringComparer.Ordinal);
        foreach (var group in entries.GroupBy(entry => NormalizeForComparison(entry.MistakenForm), StringComparer.Ordinal))
        {
            var values = group.ToArray();
            if (values.Length < 2) continue;
            var preferredCount = values.Select(entry => NormalizeForComparison(entry.PreferredTerm))
                .Distinct(StringComparer.Ordinal).Count();
            GlossaryExistingIssue issue;
            if (preferredCount > 1)
            {
                issue = new(GlossaryExistingIssueKind.Conflict, values.Length,
                    "Conflicto: la misma forma detectada apunta a términos diferentes.");
            }
            else
            {
                var portable = values.Select(ToPortable).ToArray();
                var distinctSemantic = portable.Distinct(GlossarySemanticComparer.Instance).Count();
                issue = distinctSemantic == 1
                    ? new(GlossaryExistingIssueKind.ExactDuplicate, values.Length,
                        "Grupo duplicado exacto conservado sin fusión automática.")
                    : new(GlossaryExistingIssueKind.NormalizedDuplicate, values.Length,
                        "Grupo equivalente con distinta grafía, categoría o estado; no se fusiona automáticamente.");
            }
            foreach (var entry in values) result[entry.Id] = issue;
        }
        return result;
    }

    public static string ComputeSnapshotToken(IReadOnlyList<GlossaryEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var entry in entries.OrderBy(item => item.Id, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("id", entry.Id);
                writer.WriteString("mistaken", entry.MistakenForm);
                writer.WriteString("preferred", entry.PreferredTerm);
                writer.WriteString("category", entry.Category);
                writer.WriteBoolean("active", entry.IsActive);
                writer.WriteNumber("origin", (int)entry.Origin);
                writer.WriteString("correction", entry.SourceCorrectionId);
                writer.WriteString("batch", entry.ImportBatchId);
                writer.WriteString("created", entry.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    public static string NormalizeForComparison(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var decomposed = value.Trim().Normalize(NormalizationForm.FormC).Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category is not (UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark))
                builder.Append(character);
        }
        return builder.ToString().ToUpperInvariant().Normalize(NormalizationForm.FormC);
    }

    private static string PairKey(GlossaryExchangeEntry entry) =>
        $"{NormalizeForComparison(entry.MistakenForm)}\u001F{NormalizeForComparison(entry.PreferredTerm)}";

    private static GlossaryExchangeEntry ToPortable(GlossaryEntry entry) =>
        new(entry.MistakenForm, entry.PreferredTerm, entry.Category, entry.IsActive);

    private static bool ExactSemanticEquals(GlossaryExchangeEntry left, GlossaryExchangeEntry right) =>
        string.Equals(left.MistakenForm, right.MistakenForm, StringComparison.Ordinal) &&
        string.Equals(left.PreferredTerm, right.PreferredTerm, StringComparison.Ordinal) &&
        string.Equals(left.Category, right.Category, StringComparison.Ordinal) &&
        left.IsActive == right.IsActive;

    private static void AddSeen(
        IDictionary<string, List<GlossaryExchangeEntry>> seen,
        string pair,
        GlossaryExchangeEntry entry)
    {
        if (!seen.TryGetValue(pair, out var values)) seen[pair] = values = [];
        values.Add(entry);
    }

    private sealed class GlossarySemanticComparer : IEqualityComparer<GlossaryExchangeEntry>
    {
        public static GlossarySemanticComparer Instance { get; } = new();

        public bool Equals(GlossaryExchangeEntry? left, GlossaryExchangeEntry? right) =>
            left is not null && right is not null && ExactSemanticEquals(left, right);

        public int GetHashCode(GlossaryExchangeEntry value) => HashCode.Combine(
            StringComparer.Ordinal.GetHashCode(value.MistakenForm),
            StringComparer.Ordinal.GetHashCode(value.PreferredTerm),
            StringComparer.Ordinal.GetHashCode(value.Category),
            value.IsActive);
    }
}
