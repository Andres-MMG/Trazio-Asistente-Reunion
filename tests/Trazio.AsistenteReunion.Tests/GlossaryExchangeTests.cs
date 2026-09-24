using System.Text;
using Trazio.AsistenteReunion.App;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.Tests;

public sealed class GlossaryExchangeTests
{
    [Fact]
    public async Task Serialize_RoundTripsDeterministicallyWithoutInternalMetadata()
    {
        var entries = new[]
        {
            Existing("b", "Teams", "NTeams", "Producto", false),
            Imported("a", "Meet", "Need", "Organización", true)
        };

        var first = GlossaryExchangeSerializer.Serialize(entries);
        var second = GlossaryExchangeSerializer.Serialize(entries.Reverse().ToArray());
        var text = Encoding.UTF8.GetString(first);
        await using var stream = new MemoryStream(first);
        var parsed = await GlossaryExchangeSerializer.ParseAsync(stream);

        Assert.Equal(first, second);
        Assert.False(first.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        Assert.DoesNotContain("\r\n", text, StringComparison.Ordinal);
        Assert.Contains("\"format\": \"trazio-glossary\"", text, StringComparison.Ordinal);
        Assert.Contains("\"schemaVersion\": 1", text, StringComparison.Ordinal);
        Assert.DoesNotContain("SourceCorrectionId", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ImportBatchId", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CreatedAt", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("correction", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("batch", text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["Need", "NTeams"], parsed.Rows.Select(row => row.Entry!.MistakenForm));
    }

    [Fact]
    public void Serialize_RejectsNonPortableCountAndFieldsBeforeProducingBytes()
    {
        var tooMany = Enumerable.Range(0, GlossaryExchangeSerializer.MaximumEntries + 1)
            .Select(index => Imported(index.ToString(), "Meet", $"Need {index}", "Producto", true))
            .ToArray();

        Assert.Throws<InvalidDataException>(() => GlossaryExchangeSerializer.Serialize(tooMany));

        var oversizedTerm = Imported(
            "oversized",
            new string('x', GlossaryExchangeSerializer.MaximumTermLength + 1),
            "Need",
            "Producto",
            true);
        Assert.Throws<ArgumentException>(() => GlossaryExchangeSerializer.Serialize([oversizedTerm]));

        var controlCharacter = Imported("control", "Meet", "Need\nagain", "Producto", true);
        Assert.Throws<ArgumentException>(() => GlossaryExchangeSerializer.Serialize([controlCharacter]));
    }

    [Fact]
    public void Serialize_RejectsPortableRowsWhenFinalJsonExceedsByteLimit()
    {
        var term = new string('漢', GlossaryExchangeSerializer.MaximumTermLength);
        var category = new string('漢', GlossaryExchangeSerializer.MaximumCategoryLength);
        var entries = Enumerable.Range(0, GlossaryExchangeSerializer.MaximumEntries)
            .Select(index => Imported(index.ToString(), term, term, category, true))
            .ToArray();

        var exception = Assert.Throws<InvalidDataException>(() => GlossaryExchangeSerializer.Serialize(entries));

        Assert.Contains(GlossaryExchangeSerializer.MaximumFileBytes.ToString(), exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"format\":\"trazio-glossary\",\"schemaVersion\":1,\"entries\":[],\"extra\":1}")]
    [InlineData("{\"Format\":\"trazio-glossary\",\"schemaVersion\":1,\"entries\":[]}")]
    [InlineData("{\"format\":\"trazio-glossary\",\"schemaVersion\":2,\"entries\":[]}")]
    [InlineData("[]")]
    public async Task Parse_InvalidTopLevelAbortsWholeFile(string json)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        await Assert.ThrowsAsync<InvalidDataException>(() => GlossaryExchangeSerializer.ParseAsync(stream));
    }

    [Fact]
    public async Task Parse_InvalidRowsAreRejectedWithoutDiscardingValidRows()
    {
        var json = """
            {
              "format": "trazio-glossary",
              "schemaVersion": 1,
              "entries": [
                {"mistakenForm":"Need","preferredTerm":"Meet","category":"Producto","isActive":true},
                {"MistakenForm":"Need","preferredTerm":"Meet","category":"Producto","isActive":true},
                {"mistakenForm":"Bad\u0000value","preferredTerm":"Meet","category":"Producto","isActive":true},
                {"mistakenForm":"Need","preferredTerm":"Meet","category":"Producto","isActive":true,"extra":1}
              ]
            }
            """;
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var parsed = await GlossaryExchangeSerializer.ParseAsync(stream);

        Assert.NotNull(parsed.Rows[0].Entry);
        Assert.All(parsed.Rows.Skip(1), row => Assert.Null(row.Entry));
        Assert.Contains("mayúsculas", parsed.Rows[1].RejectionReason, StringComparison.Ordinal);
        Assert.Contains("control", parsed.Rows[2].RejectionReason, StringComparison.Ordinal);
        Assert.Contains("no está permitida", parsed.Rows[3].RejectionReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Parse_AcceptsBomAndNfcNormalizesTrimmedFields()
    {
        var body = Encoding.UTF8.GetBytes("""
            {"format":"trazio-glossary","schemaVersion":1,"entries":[
              {"mistakenForm":"  reunión  ","preferredTerm":" Reunión ","category":" Técnico ","isActive":false}
            ]}
            """);
        var bytes = Encoding.UTF8.Preamble.ToArray().Concat(body).ToArray();
        await using var stream = new MemoryStream(bytes);

        var entry = Assert.Single((await GlossaryExchangeSerializer.ParseAsync(stream)).Rows).Entry!;

        Assert.Equal("reunión", entry.MistakenForm);
        Assert.Equal("Reunión", entry.PreferredTerm);
        Assert.Equal("Técnico", entry.Category);
    }

    [Fact]
    public async Task Parse_RejectsInvalidUtf8AndHonorsByteLimitAndCancellation()
    {
        await using var invalidUtf8 = new MemoryStream([0xFF, 0xFE, 0xFA]);
        await Assert.ThrowsAsync<InvalidDataException>(() => GlossaryExchangeSerializer.ParseAsync(invalidUtf8));

        await using var oversized = new MemoryStream(new byte[GlossaryExchangeSerializer.MaximumFileBytes + 1]);
        await Assert.ThrowsAsync<InvalidDataException>(() => GlossaryExchangeSerializer.ParseAsync(oversized));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await using var cancelled = new MemoryStream(Encoding.UTF8.GetBytes("{}"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            GlossaryExchangeSerializer.ParseAsync(cancelled, cancellation.Token));
    }

    [Fact]
    public async Task Parse_EnforcesRowCountAndFieldLengths()
    {
        var entry = "{\"mistakenForm\":\"Need\",\"preferredTerm\":\"Meet\",\"category\":\"Producto\",\"isActive\":true}";
        var tooMany = $"{{\"format\":\"trazio-glossary\",\"schemaVersion\":1,\"entries\":[{string.Join(',', Enumerable.Repeat(entry, GlossaryExchangeSerializer.MaximumEntries + 1))}]}}";
        await using var countStream = new MemoryStream(Encoding.UTF8.GetBytes(tooMany));
        await Assert.ThrowsAsync<InvalidDataException>(() => GlossaryExchangeSerializer.ParseAsync(countStream));

        var tooLong = new string('x', GlossaryExchangeSerializer.MaximumTermLength + 1);
        var invalidRow = $"{{\"format\":\"trazio-glossary\",\"schemaVersion\":1,\"entries\":[{{\"mistakenForm\":\"{tooLong}\",\"preferredTerm\":\"Meet\",\"category\":\"Producto\",\"isActive\":true}}]}}";
        await using var lengthStream = new MemoryStream(Encoding.UTF8.GetBytes(invalidRow));
        var parsed = await GlossaryExchangeSerializer.ParseAsync(lengthStream);
        Assert.Null(Assert.Single(parsed.Rows).Entry);
        Assert.Contains("120", parsed.Rows[0].RejectionReason, StringComparison.Ordinal);
    }

    [Fact]
    public void GlossaryEntry_RequiresExactlyOneProvenance()
    {
        var createdAt = DateTimeOffset.UtcNow;
        Assert.Throws<ArgumentException>(() => new GlossaryEntry(
            "id", "Meet", "Need", "Producto", true,
            GlossaryEntryOrigin.TranscriptCorrection, null, null, createdAt));
        Assert.Throws<ArgumentException>(() => new GlossaryEntry(
            "id", "Meet", "Need", "Producto", true,
            GlossaryEntryOrigin.ImportedFile, "correction", "batch", createdAt));
    }

    [Fact]
    public void Preview_ClassifiesExactNormalizedConflictRejectedAndNewConservatively()
    {
        var existing = new[]
        {
            Existing("one", "Meet", "Need", "Producto", true),
            Existing("two", "Trazio", "Trasio", "Producto", true)
        };
        var parsed = new GlossaryImportParseResult(
        [
            new(1, new("Need", "Meet", "Producto", true), null),
            new(2, new("néed", "MEET", "Otra", false), null),
            new(3, new("Nuevo", "Correcto", "General", true), null),
            new(4, new("Interno", "Uno", "General", true), null),
            new(5, new("internó", "Dos", "General", true), null),
            new(6, null, "Fila inválida")
        ]);

        var preview = GlossaryExchangePlanner.CreatePreview(existing, parsed);

        Assert.Equal(GlossaryImportClassification.ExactDuplicate, preview.Items[0].Classification);
        Assert.Equal(GlossaryImportClassification.NormalizedDuplicate, preview.Items[1].Classification);
        Assert.Equal(GlossaryImportClassification.New, preview.Items[2].Classification);
        Assert.Equal(GlossaryImportClassification.Conflict, preview.Items[3].Classification);
        Assert.Equal(GlossaryImportClassification.Conflict, preview.Items[4].Classification);
        Assert.Equal(GlossaryImportClassification.Rejected, preview.Items[5].Classification);
        Assert.Equal([3], preview.Items.Where(item => item.Classification == GlossaryImportClassification.New).Select(item => item.RowNumber));
    }

    [Fact]
    public void Preview_DoesNotCollapseSpacesOrUseFuzzyMatching()
    {
        var existing = new[] { Existing("one", "Meet", "Need now", "Producto", true) };
        var parsed = new GlossaryImportParseResult(
        [
            new(1, new("Need  now", "Meet", "Producto", true), null),
            new(2, new("Needs now", "Meet", "Producto", true), null)
        ]);

        var preview = GlossaryExchangePlanner.CreatePreview(existing, parsed);

        Assert.All(preview.Items, item => Assert.Equal(GlossaryImportClassification.New, item.Classification));
    }

    [Fact]
    public void ExistingAnalysisMarksEveryLegacyDuplicateAndConflictWithoutMerging()
    {
        var entries = new[]
        {
            Existing("a", "Meet", "Need", "Producto", true),
            Existing("b", "MEET", "néed", "Otra", false),
            Existing("c", "Teams", "NTeams", "Producto", true),
            Existing("d", "Zoom", "nteams", "Producto", true)
        };

        var issues = GlossaryExchangePlanner.AnalyzeExisting(entries);

        Assert.Equal(4, issues.Count);
        Assert.Equal(GlossaryExistingIssueKind.NormalizedDuplicate, issues["a"].Kind);
        Assert.Equal(GlossaryExistingIssueKind.NormalizedDuplicate, issues["b"].Kind);
        Assert.Equal(GlossaryExistingIssueKind.Conflict, issues["c"].Kind);
        Assert.Equal(GlossaryExistingIssueKind.Conflict, issues["d"].Kind);
    }

    [Fact]
    public async Task AtomicWriter_CancellationPreservesDestinationAndLeavesNoTemporaryFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "trazio-glossary-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "dictionary.json");
            await File.WriteAllTextAsync(path, "previous");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                GlossaryExchangeFileWriter.WriteAtomicallyAsync(path, Encoding.UTF8.GetBytes("replacement"), cancellation.Token));

            Assert.Equal("previous", await File.ReadAllTextAsync(path));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task AtomicWriter_ReplacesDestinationAndLeavesNoTemporaryFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "trazio-glossary-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "dictionary.json");
            await File.WriteAllTextAsync(path, "previous");

            await GlossaryExchangeFileWriter.WriteAtomicallyAsync(path, Encoding.UTF8.GetBytes("replacement"));

            Assert.Equal("replacement", await File.ReadAllTextAsync(path));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
        }
        finally { Directory.Delete(directory, true); }
    }

    private static GlossaryEntry Existing(
        string id,
        string preferred,
        string mistaken,
        string category,
        bool active) => new(
            id,
            preferred,
            mistaken,
            category,
            active,
            GlossaryEntryOrigin.TranscriptCorrection,
            "correction-" + id,
            null,
            new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero));

    private static GlossaryEntry Imported(
        string id,
        string preferred,
        string mistaken,
        string category,
        bool active) => new(
            id,
            preferred,
            mistaken,
            category,
            active,
            GlossaryEntryOrigin.ImportedFile,
            null,
            "batch-" + id,
            new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero));
}
