using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.Tests;

public sealed class ReviewStoreTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "trazio-review-" + Guid.NewGuid().ToString("N"));
    private AesContentProtector _protector = null!;
    private SqliteSessionStore _store = null!;
    private string DatabasePath => Path.Combine(_directory, "review.db");

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
    public async Task SaveAndUndoCorrection_PreservesOriginalAndRevisionAudit()
    {
        var segment = await CreateSegmentAsync("raw model text");

        var saved = await _store.SaveCorrectionAsync(segment.Id, "corrected text", "Andrea");
        var corrected = Assert.Single(await _store.GetReviewedSegmentsAsync(segment.SessionId));
        var undo = await _store.UndoCorrectionAsync(segment.Id, "Andrea");
        var restored = Assert.Single(await _store.GetReviewedSegmentsAsync(segment.SessionId));
        var history = await _store.GetCorrectionsAsync(segment.Id);

        Assert.Equal(CorrectionAction.SetText, saved.Action);
        Assert.Equal("corrected text", corrected.EffectiveText);
        Assert.Equal("raw model text", corrected.Segment.Text);
        Assert.Equal(CorrectionAction.Undo, undo.Action);
        Assert.Equal("raw model text", restored.EffectiveText);
        Assert.Equal([1, 2], history.Select(item => item.Revision));
        Assert.All(history, item => Assert.Equal("Andrea", item.EditorName));
    }

    [Fact]
    public async Task CorrectionAndGlossary_AreEncryptedAndKeepProvenance()
    {
        var segment = await CreateSegmentAsync("Trazzio original");
        var correction = await _store.SaveCorrectionAsync(segment.Id, "Trazio corrected", "Private Editor");
        var glossary = await _store.AddGlossaryEntryAsync("Trazio", "Trazzio", "Product", true, correction.Id);

        var loaded = Assert.Single(await _store.ListGlossaryAsync());
        var raw = System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(DatabasePath));

        Assert.Equal(glossary, loaded);
        Assert.Equal(correction.Id, loaded.SourceCorrectionId);
        Assert.DoesNotContain("Trazio corrected", raw);
        Assert.DoesNotContain("Private Editor", raw);
        Assert.DoesNotContain("Trazzio", raw);
        Assert.DoesNotContain("Product", raw);
    }

    [Fact]
    public async Task ReviewedOldRecord_WithoutCorrections_UsesOriginalTextAndSpeaker()
    {
        var segment = await CreateSegmentAsync("legacy", "Andrea");

        var reviewed = Assert.Single(await _store.GetReviewedSegmentsAsync(segment.SessionId));

        Assert.Equal("legacy", reviewed.EffectiveText);
        Assert.False(reviewed.IsCorrected);
        Assert.Equal("Andrea", reviewed.Segment.SpeakerName);
    }

    [Fact]
    public async Task InitializeAsync_PreReviewSchema_CreatesReviewTablesWithoutChangingOldSegments()
    {
        var path = Path.Combine(_directory, "legacy.db");
        await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE sessions (
                  id TEXT PRIMARY KEY, title_nonce BLOB NOT NULL, title_cipher BLOB NOT NULL, title_tag BLOB NOT NULL,
                  started_at TEXT NOT NULL, ended_at TEXT NULL, state INTEGER NOT NULL);
                CREATE TABLE segments (
                  id TEXT PRIMARY KEY, session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
                  source INTEGER NOT NULL, sequence INTEGER NOT NULL, start_ms INTEGER NOT NULL, end_ms INTEGER NOT NULL,
                  text_nonce BLOB NOT NULL, text_cipher BLOB NOT NULL, text_tag BLOB NOT NULL, created_at TEXT NOT NULL,
                  UNIQUE(id));
                """;
            await command.ExecuteNonQueryAsync();
        }

        var migrated = new SqliteSessionStore(path, _protector);
        await migrated.InitializeAsync();
        var session = new MeetingSession("legacy-session", "Legacy", DateTimeOffset.UtcNow, null, SessionState.Completed);
        await migrated.CreateSessionAsync(session);
        await migrated.SaveSegmentAsync(new("legacy-segment", session.Id, AudioSourceKind.SystemOutput, 0,
            TimeSpan.Zero, TimeSpan.FromSeconds(1), "old text", DateTimeOffset.UtcNow));

        var reviewed = Assert.Single(await migrated.GetReviewedSegmentsAsync(session.Id));
        Assert.Equal("old text", reviewed.EffectiveText);
        Assert.Empty(await migrated.ListGlossaryAsync());
    }

    [Fact]
    public async Task TranscriptExport_UsesCorrectedTextWhileOriginalRemainsRecoverable()
    {
        var segment = await CreateSegmentAsync("stale original");
        await _store.SaveCorrectionAsync(segment.Id, "approved correction", "Andrea");

        var reviewed = await _store.GetReviewedSegmentsAsync(segment.SessionId);
        var exported = Trazio.AsistenteReunion.App.TranscriptExport.CreatePlainText(reviewed);
        var original = Assert.Single(await _store.GetSegmentsAsync(segment.SessionId));

        Assert.Contains("approved correction", exported);
        Assert.DoesNotContain("stale original", exported);
        Assert.Equal("stale original", original.Text);
    }

    [Fact]
    public async Task InitializeAsync_Twice_IsIdempotentAndPreservesReviewData()
    {
        var segment = await CreateSegmentAsync("raw");
        await _store.SaveCorrectionAsync(segment.Id, "corrected", "Andrea");

        await _store.InitializeAsync();
        await _store.InitializeAsync();

        var reviewed = Assert.Single(await _store.GetReviewedSegmentsAsync(segment.SessionId));
        Assert.Equal("corrected", reviewed.EffectiveText);
        Assert.Equal("raw", reviewed.Segment.Text);
    }
    private async Task<TranscriptSegment> CreateSegmentAsync(string text, string? speaker = null)
    {
        var session = new MeetingSession(Guid.NewGuid().ToString("N"), "Review", DateTimeOffset.UtcNow, null,
            SessionState.Completed, speaker);
        await _store.CreateSessionAsync(session);
        var segment = new TranscriptSegment(Guid.NewGuid().ToString("N"), session.Id,
            speaker is null ? AudioSourceKind.SystemOutput : AudioSourceKind.Microphone,
            0, TimeSpan.Zero, TimeSpan.FromSeconds(1), text, DateTimeOffset.UtcNow, speaker);
        await _store.SaveSegmentAsync(segment);
        return segment;
    }
}