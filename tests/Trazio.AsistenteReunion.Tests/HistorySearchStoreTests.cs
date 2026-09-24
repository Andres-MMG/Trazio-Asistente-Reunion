using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.Tests;

public sealed class HistorySearchStoreTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "trazio-search-" + Guid.NewGuid().ToString("N"));
    private AesContentProtector _protector = null!;
    private SqliteSessionStore _store = null!;
    private string DatabasePath => Path.Combine(_directory, "search.db");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        _protector = new(RandomNumberGenerator.GetBytes(32));
        _store = new(DatabasePath, _protector);
        await _store.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        _protector.Dispose();
        Directory.Delete(_directory, true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task SearchHistory_TitleOriginalCorrectionAndUndo_UsesOnlyEffectiveOriginalTranscript()
    {
        var (session, segment) = await CreateSegmentAsync("Reunión Café", "Necesitamos revisar Need mañana");

        var title = await _store.SearchHistoryAsync("reunion cafe");
        var original = await _store.SearchHistoryAsync("NECESITAMOS");
        await _store.SaveCorrectionAsync(segment.Id, "Necesitamos revisar Meet mañana", "Andrea");
        var hiddenOriginal = await _store.SearchHistoryAsync("need manana");
        var correction = await _store.SearchHistoryAsync("MEET MAÑANA");
        await _store.UndoCorrectionAsync(segment.Id, "Andrea");
        var restored = await _store.SearchHistoryAsync("need mañana");

        Assert.Equal(HistorySearchMatchKind.SessionTitle, Assert.Single(title.Hits).MatchKind);
        Assert.Equal(segment.Id, Assert.Single(original.Hits).SegmentId);
        Assert.Empty(hiddenOriginal.Hits);
        Assert.Equal("Necesitamos revisar Meet mañana", Assert.Single(correction.Hits).Snippet);
        Assert.Equal(segment.Id, Assert.Single(restored.Hits).SegmentId);
        Assert.All([title, original, hiddenOriginal, correction, restored], result => Assert.False(result.IsTruncated));
        Assert.Equal(session.Id, Assert.Single(title.Hits).SessionId);
    }

    [Fact]
    public async Task SearchHistory_ModelRevisionText_IsExcluded()
    {
        var (session, _) = await CreateSegmentAsync("Sesión original", "texto base");
        var revision = await _store.StartModelRevisionAsync(
            session.Id,
            AudioSourceKind.SystemOutput,
            "model.bin",
            null,
            "es");
        await _store.SaveModelRevisionSegmentAsync(new(
            "revision-segment",
            revision.Id,
            0,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(2),
            "contenido exclusivo alternativo"));
        await _store.FinishModelRevisionAsync(
            revision.Id,
            ModelRevisionStatus.Succeeded,
            DateTimeOffset.UtcNow,
            TimeSpan.FromSeconds(1),
            null);

        var result = await _store.SearchHistoryAsync("exclusivo alternativo");

        Assert.Empty(result.Hits);
    }

    [Fact]
    public async Task SearchHistory_MoreThanLimit_ReturnsDeterministicBoundedPrefixAndTruncation()
    {
        var session = await CreateSessionAsync("Reunión extensa", new DateTimeOffset(2026, 9, 24, 10, 0, 0, TimeSpan.Zero));
        for (var index = 0; index < 101; index++)
            await SaveSegmentAsync(session, $"match {index:D3}", index, TimeSpan.FromSeconds(index));

        var result = await _store.SearchHistoryAsync("match", 100);

        Assert.Equal(101, result.MatchCount);
        Assert.True(result.IsTruncated);
        Assert.Equal(100, result.Hits.Count);
        Assert.Equal("match 000", result.Hits[0].Snippet);
        Assert.Equal("match 099", result.Hits[^1].Snippet);
        Assert.Equal(
            Enumerable.Range(0, 100).Select(index => (TimeSpan?)TimeSpan.FromSeconds(index)),
            result.Hits.Select(item => item.Start));
    }

    [Fact]
    public async Task SearchHistory_LimitAbovePrivacyContract_IsRejectedByStore()
    {
        await CreateSegmentAsync("Límite", "texto buscable");

        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            _store.SearchHistoryAsync("buscable", HistorySearchText.MaximumResults + 1));

        Assert.Equal("maximumResults", exception.ParamName);
    }

    [Fact]
    public async Task SearchHistory_MultipleSessionsAndKinds_UsesStableNewestFirstOrder()
    {
        var older = await CreateSessionAsync("match antigua", new DateTimeOffset(2026, 9, 23, 10, 0, 0, TimeSpan.Zero));
        await SaveSegmentAsync(older, "match segmento antiguo", 0, TimeSpan.FromSeconds(2));
        var newer = await CreateSessionAsync("match nueva", new DateTimeOffset(2026, 9, 24, 10, 0, 0, TimeSpan.Zero));
        await SaveSegmentAsync(newer, "match segmento nuevo", 0, TimeSpan.FromSeconds(1));

        var result = await _store.SearchHistoryAsync("match");

        Assert.Collection(
            result.Hits,
            hit =>
            {
                Assert.Equal(newer.Id, hit.SessionId);
                Assert.Equal(HistorySearchMatchKind.SessionTitle, hit.MatchKind);
            },
            hit =>
            {
                Assert.Equal(newer.Id, hit.SessionId);
                Assert.Equal(HistorySearchMatchKind.TranscriptSegment, hit.MatchKind);
            },
            hit =>
            {
                Assert.Equal(older.Id, hit.SessionId);
                Assert.Equal(HistorySearchMatchKind.SessionTitle, hit.MatchKind);
            },
            hit =>
            {
                Assert.Equal(older.Id, hit.SessionId);
                Assert.Equal(HistorySearchMatchKind.TranscriptSegment, hit.MatchKind);
            });
    }

    [Fact]
    public async Task SearchHistory_CancelledToken_StopsWithoutPublishingResults()
    {
        await CreateSegmentAsync("Cancelación", "texto buscable");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _store.SearchHistoryAsync("buscable", cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task SearchHistory_CorruptEncryptedSegment_FailsWholeSearchClosed()
    {
        var (_, segment) = await CreateSegmentAsync("Integridad", "texto íntegro");
        await using (var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE segments SET text_cipher=zeroblob(length(text_cipher)) WHERE id=$id";
            command.Parameters.AddWithValue("$id", segment.Id);
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAnyAsync<CryptographicException>(() => _store.SearchHistoryAsync("integridad"));
    }

    [Fact]
    public async Task SearchHistory_QueryAndResults_AreNotAddedToDatabaseSchemaOrPlaintext()
    {
        await CreateSegmentAsync("Título privado", "Búsqueda única protegida");

        var result = await _store.SearchHistoryAsync("busqueda unica");
        var raw = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(DatabasePath));
        var schemaNames = new List<string>();
        await using (var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM sqlite_master WHERE type IN ('table','index') ORDER BY name";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) schemaNames.Add(reader.GetString(0));
        }

        Assert.Single(result.Hits);
        Assert.DoesNotContain("busqueda unica", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(schemaNames, name => name.Contains("search", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<(MeetingSession Session, TranscriptSegment Segment)> CreateSegmentAsync(string title, string text)
    {
        var session = await CreateSessionAsync(title, DateTimeOffset.UtcNow);
        var segment = await SaveSegmentAsync(session, text, 0, TimeSpan.Zero);
        return (session, segment);
    }

    private async Task<MeetingSession> CreateSessionAsync(string title, DateTimeOffset startedAt)
    {
        var session = new MeetingSession(
            Guid.NewGuid().ToString("N"),
            title,
            startedAt,
            startedAt.AddMinutes(30),
            SessionState.Completed);
        await _store.CreateSessionAsync(session);
        return session;
    }

    private async Task<TranscriptSegment> SaveSegmentAsync(
        MeetingSession session,
        string text,
        long sequence,
        TimeSpan start)
    {
        var segment = new TranscriptSegment(
            Guid.NewGuid().ToString("N"),
            session.Id,
            AudioSourceKind.SystemOutput,
            sequence,
            start,
            start.Add(TimeSpan.FromSeconds(1)),
            text,
            session.StartedAt.Add(start));
        await _store.SaveSegmentAsync(segment);
        return segment;
    }
}
