using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.Tests;

public sealed class BatchReviewStoreTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "trazio-batch-review-" + Guid.NewGuid().ToString("N"));
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
    public async Task ListPendingReviews_OrdersLimitsAndReportsTruncation()
    {
        var older = await CreateSessionAsync("Older meeting", DateTimeOffset.UtcNow.AddHours(-2));
        await CreateSegmentAsync(older, "older second", 2, TimeSpan.FromSeconds(20));
        await CreateSegmentAsync(older, "older first", 1, TimeSpan.FromSeconds(10));
        var newer = await CreateSessionAsync("Newer meeting", DateTimeOffset.UtcNow.AddHours(-1));
        TranscriptSegment hiddenAfterLimit = null!;
        for (var index = 0; index < 101; index++)
        {
            var segment = await CreateSegmentAsync(newer, $"newer {index:D3}", index, TimeSpan.FromSeconds(index));
            if (index == 100) hiddenAfterLimit = segment;
        }

        var result = await _store.ListPendingSegmentReviewsAsync(100);

        Assert.Equal(103, result.TotalCount);
        Assert.True(result.IsTruncated);
        Assert.Equal(100, result.Items.Count);
        Assert.All(result.Items, item => Assert.Equal(newer.Id, item.SessionId));
        Assert.Equal(Enumerable.Range(0, 100).Select(index => (long)index), result.Items.Select(item => item.Sequence));
        Assert.All(result.Items, item => Assert.Equal(0, item.ExpectedDecisionRevision));

        await using (var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var corrupt = connection.CreateCommand();
            corrupt.CommandText = "UPDATE segments SET text_cipher=zeroblob(length(text_cipher)) WHERE id=$segment";
            corrupt.Parameters.AddWithValue("$segment", hiddenAfterLimit.Id);
            await corrupt.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAnyAsync<CryptographicException>(() =>
            _store.ListPendingSegmentReviewsAsync(100));
    }

    [Fact]
    public async Task ListPendingReviews_ExcludesActiveSessionsAndAnyCorrectionHistory()
    {
        var completed = await CreateSessionAsync("Completed", DateTimeOffset.UtcNow.AddMinutes(-4));
        var pending = await CreateSegmentAsync(completed, "pending", 0, TimeSpan.Zero);
        var corrected = await CreateSegmentAsync(completed, "corrected", 1, TimeSpan.FromSeconds(1));
        var undone = await CreateSegmentAsync(completed, "undone", 2, TimeSpan.FromSeconds(2));
        await _store.SaveCorrectionAsync(corrected.Id, "human correction", "Andrea");
        await _store.SaveCorrectionAsync(undone.Id, "temporary correction", "Andrea");
        await _store.UndoCorrectionAsync(undone.Id, "Andrea");
        var recording = await CreateSessionAsync("Recording", DateTimeOffset.UtcNow, SessionState.Recording);
        await CreateSegmentAsync(recording, "active", 0, TimeSpan.Zero);
        var paused = await CreateSessionAsync("Paused", DateTimeOffset.UtcNow, SessionState.Paused);
        await CreateSegmentAsync(paused, "paused", 0, TimeSpan.Zero);
        var interrupted = await CreateSessionAsync(
            "Interrupted",
            DateTimeOffset.UtcNow.AddMinutes(-1),
            SessionState.Interrupted);
        var interruptedPending = await CreateSegmentAsync(interrupted, "interrupted pending", 0, TimeSpan.Zero);

        var result = await _store.ListPendingSegmentReviewsAsync();

        Assert.Equal([interruptedPending.Id, pending.Id], result.Items.Select(item => item.SegmentId));
        Assert.Equal(2, result.TotalCount);
        Assert.False(result.IsTruncated);
    }

    [Fact]
    public async Task ApproveAndReopen_AppendEncryptedDecisionsAndChangePendingState()
    {
        var session = await CreateSessionAsync("Private meeting title", DateTimeOffset.UtcNow);
        var segment = await CreateSegmentAsync(session, "private transcript text", 0, TimeSpan.Zero);

        var approved = await _store.ApproveOriginalSegmentAsync(session.Id, segment.Id, 0, "Private Reviewer");
        var afterApproval = await _store.ListPendingSegmentReviewsAsync();
        var reopened = await _store.ReopenOriginalSegmentAsync(session.Id, segment.Id, 1, "Private Reviewer");
        var afterReopen = await _store.ListPendingSegmentReviewsAsync();
        var decisions = await _store.GetSegmentReviewDecisionsAsync(segment.Id);
        var raw = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(DatabasePath));

        Assert.Equal(SegmentReviewWriteStatus.Applied, approved.Status);
        Assert.Empty(afterApproval.Items);
        Assert.Equal(SegmentReviewWriteStatus.Applied, reopened.Status);
        var pending = Assert.Single(afterReopen.Items);
        Assert.Equal(2, pending.ExpectedDecisionRevision);
        Assert.Equal(
            [SegmentReviewDecisionAction.ApproveOriginal, SegmentReviewDecisionAction.Reopen],
            decisions.Select(item => item.Action));
        Assert.Equal([1, 2], decisions.Select(item => item.Revision));
        Assert.All(decisions, item => Assert.Equal("Private Reviewer", item.ReviewerName));
        Assert.DoesNotContain("Private Reviewer", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("Private meeting title", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("private transcript text", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DecisionWrites_RevalidateExpectedRevisionEligibilityAndMissingRows()
    {
        var session = await CreateSessionAsync("Review", DateTimeOffset.UtcNow);
        var segment = await CreateSegmentAsync(session, "original", 0, TimeSpan.Zero);
        var approved = await _store.ApproveOriginalSegmentAsync(session.Id, segment.Id, 0, "Andrea");

        var duplicateApproval = await _store.ApproveOriginalSegmentAsync(session.Id, segment.Id, 0, "Andrea");
        var staleReopen = await _store.ReopenOriginalSegmentAsync(session.Id, segment.Id, 0, "Andrea");
        await _store.SaveCorrectionAsync(segment.Id, "corrected", "Andrea");
        var correctionRace = await _store.ReopenOriginalSegmentAsync(session.Id, segment.Id, approved.CurrentRevision, "Andrea");
        var wrongSession = await _store.ApproveOriginalSegmentAsync("other-session", segment.Id, 0, "Andrea");
        var missing = await _store.ApproveOriginalSegmentAsync(session.Id, "missing-segment", 0, "Andrea");

        Assert.Equal(SegmentReviewWriteStatus.AlreadyCurrent, duplicateApproval.Status);
        Assert.Equal(SegmentReviewWriteStatus.StateChanged, staleReopen.Status);
        Assert.Equal(SegmentReviewWriteStatus.StateChanged, correctionRace.Status);
        Assert.Equal(approved.CurrentRevision, correctionRace.CurrentRevision);
        Assert.Equal(SegmentReviewWriteStatus.Missing, wrongSession.Status);
        Assert.Equal(SegmentReviewWriteStatus.Missing, missing.Status);
        Assert.Single(await _store.GetSegmentReviewDecisionsAsync(segment.Id));
    }

    [Fact]
    public async Task ConcurrentApprovals_CreateExactlyOneTransition()
    {
        var session = await CreateSessionAsync("Review", DateTimeOffset.UtcNow);
        var segment = await CreateSegmentAsync(session, "original", 0, TimeSpan.Zero);
        var secondStore = new SqliteSessionStore(DatabasePath, _protector);

        var results = await Task.WhenAll(
            _store.ApproveOriginalSegmentAsync(session.Id, segment.Id, 0, "Andrea"),
            secondStore.ApproveOriginalSegmentAsync(session.Id, segment.Id, 0, "Andrea"));

        Assert.Single(results, item => item.Status == SegmentReviewWriteStatus.Applied);
        Assert.Single(results, item => item.Status == SegmentReviewWriteStatus.AlreadyCurrent);
        Assert.Single(await _store.GetSegmentReviewDecisionsAsync(segment.Id));
    }

    [Fact]
    public async Task ListPendingReviews_CancellationAndCorruptionFailClosed()
    {
        var session = await CreateSessionAsync("Review", DateTimeOffset.UtcNow);
        var segment = await CreateSegmentAsync(session, "original", 0, TimeSpan.Zero);
        await _store.ApproveOriginalSegmentAsync(session.Id, segment.Id, 0, "Andrea");
        await _store.ReopenOriginalSegmentAsync(session.Id, segment.Id, 1, "Andrea");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _store.ListPendingSegmentReviewsAsync(cancellationToken: cancelled.Token));

        await using (var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE segment_review_decisions SET reviewer_cipher=zeroblob(length(reviewer_cipher)) WHERE segment_id=$segment AND revision=2";
            command.Parameters.AddWithValue("$segment", segment.Id);
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAnyAsync<CryptographicException>(() => _store.ListPendingSegmentReviewsAsync());
    }

    [Fact]
    public async Task DeleteSession_CascadesReviewDecisions()
    {
        var session = await CreateSessionAsync("Review", DateTimeOffset.UtcNow);
        var segment = await CreateSegmentAsync(session, "original", 0, TimeSpan.Zero);
        await _store.ApproveOriginalSegmentAsync(session.Id, segment.Id, 0, "Andrea");

        await _store.DeleteSessionAsync(session.Id);

        Assert.Empty(await _store.GetSegmentReviewDecisionsAsync(segment.Id));
        await using var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM segment_review_decisions";
        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task InitializeAsync_Beta10DatabaseAddsDecisionTableIdempotently()
    {
        await using (var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var drop = connection.CreateCommand();
            drop.CommandText = "DROP TABLE segment_review_decisions";
            await drop.ExecuteNonQueryAsync();
        }

        await _store.InitializeAsync();
        await _store.InitializeAsync();

        await using var reopened = new SqliteConnection($"Data Source={DatabasePath};Pooling=False");
        await reopened.OpenAsync();
        await using var exists = reopened.CreateCommand();
        exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='segment_review_decisions'";
        Assert.Equal(1L, (long)(await exists.ExecuteScalarAsync())!);
    }

    private async Task<MeetingSession> CreateSessionAsync(
        string title,
        DateTimeOffset startedAt,
        SessionState state = SessionState.Completed)
    {
        var session = new MeetingSession(
            Guid.NewGuid().ToString("N"),
            title,
            startedAt,
            state is SessionState.Completed or SessionState.Interrupted ? startedAt.AddMinutes(30) : null,
            state,
            "Andrea");
        await _store.CreateSessionAsync(session);
        return session;
    }

    private async Task<TranscriptSegment> CreateSegmentAsync(
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
