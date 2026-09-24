using Trazio.AsistenteReunion.App;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.Tests;

public sealed class BatchReviewPresentationTests
{
    private static readonly DateTimeOffset StartedAt =
        new(2026, 9, 24, 12, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Create_TruncatedPendingResult_FormatsRowsAndHonestStatus()
    {
        var pending = new PendingSegmentReview(
            "session",
            "segment",
            "Planificación",
            StartedAt,
            AudioSourceKind.SystemOutput,
            7,
            TimeSpan.FromSeconds(75),
            TimeSpan.FromSeconds(81),
            "Texto pendiente de revisión",
            null,
            2);

        var state = PendingReviewPresenter.Create(
            new([pending], 142, IsTruncated: true),
            TimeZoneInfo.Utc);
        var item = Assert.Single(state.Items);

        Assert.Equal("Planificación", item.MeetingTitle);
        Assert.Equal("2026-09-24 12:30 · Audio del equipo · 01:15", item.Details);
        Assert.Equal("Texto pendiente de revisión", item.Snippet);
        Assert.Equal("Pendiente de revisión", item.StateLabel);
        Assert.Contains("Planificación", item.AutomationName, StringComparison.Ordinal);
        Assert.Contains("pendiente de revisión", item.AutomationName, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("primeros 1 de 142", state.Status, StringComparison.Ordinal);
        Assert.True(state.IsTruncated);
    }

    [Fact]
    public void Create_EmptyAndCompleteStates_AreExplicit()
    {
        var empty = PendingReviewPresenter.Create(new([], 0, IsTruncated: false));

        Assert.Empty(empty.Items);
        Assert.Equal("Revisión completa: no quedan segmentos pendientes.", empty.Status);
        Assert.False(empty.IsTruncated);
    }

    [Fact]
    public void PendingItem_AccessibleNameKeepsCompleteTextWhenVisibleSnippetIsTruncated()
    {
        var text = new string('a', 180) + " final completo";
        var pending = new PendingSegmentReview(
            "session",
            "segment",
            "Meeting",
            StartedAt,
            AudioSourceKind.SystemOutput,
            0,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(1),
            text,
            null,
            0);

        var item = PendingReviewItem.From(pending, TimeZoneInfo.Utc);

        Assert.DoesNotContain("final completo", item.Snippet, StringComparison.Ordinal);
        Assert.Contains(text, item.AutomationName, StringComparison.Ordinal);
    }

    [Fact]
    public void NavigationIntent_UsesStableIdsOriginalRevisionAndNeverAutoplays()
    {
        var pending = new PendingSegmentReview(
            "session",
            "segment",
            "Meeting",
            StartedAt,
            AudioSourceKind.Microphone,
            2,
            TimeSpan.FromSeconds(8),
            TimeSpan.FromSeconds(10),
            "texto",
            "Andrea",
            0);
        var item = PendingReviewItem.From(pending, TimeZoneInfo.Utc);

        var intent = PendingReviewNavigationIntent.From(item);

        Assert.Equal("session", intent.SessionId);
        Assert.Equal("segment", intent.SegmentId);
        Assert.Equal(AudioSourceKind.Microphone, intent.Source);
        Assert.True(intent.UseOriginalRevision);
        Assert.False(intent.AutoPlay);
    }

    [Fact]
    public void ReviewActions_OnlyAllowOriginalApprovalOrReopeningWithoutCorrectionHistory()
    {
        var segment = Segment();
        var pending = new ReviewedTranscriptSegment(segment, null);
        var approvedDecision = new SegmentReviewDecision(
            "decision",
            segment.Id,
            segment.SessionId,
            1,
            SegmentReviewDecisionAction.ApproveOriginal,
            "Andrea",
            StartedAt);
        var approved = new ReviewedTranscriptSegment(segment, null, approvedDecision);
        var correction = new TranscriptCorrection(
            "correction",
            segment.Id,
            segment.SessionId,
            1,
            CorrectionAction.Undo,
            null,
            "Andrea",
            StartedAt);
        var corrected = new ReviewedTranscriptSegment(segment, correction, approvedDecision);

        Assert.Equal(new(true, false, true), SegmentReviewActionsPresenter.Create(pending, SessionState.Completed, false, false));
        Assert.Equal(new(false, true, true), SegmentReviewActionsPresenter.Create(approved, SessionState.Interrupted, false, false));
        Assert.Equal(new(false, false, false), SegmentReviewActionsPresenter.Create(corrected, SessionState.Completed, false, false));
        Assert.Equal(new(false, false, false), SegmentReviewActionsPresenter.Create(pending, SessionState.Completed, true, false));
        Assert.Equal(new(false, false, true), SegmentReviewActionsPresenter.Create(pending, SessionState.Completed, false, true));
        Assert.Equal(new(false, false, false), SegmentReviewActionsPresenter.Create(pending, SessionState.Recording, false, false));
        Assert.Equal(new(false, false, false), SegmentReviewActionsPresenter.Create(pending, SessionState.Paused, false, false));
    }

    [Fact]
    public void NavigationGuard_UnsavedDraftPreservesSelectionAndEditorUntilExplicitAction()
    {
        var guard = new CorrectionDraftNavigationGuard();

        guard.SetContext(
            "session-a",
            "segment-a",
            AudioSourceKind.Microphone,
            "texto guardado");
        guard.ObserveEditor("borrador todavía no guardado");
        var blocked = guard.ResolveSegmentTransition(
            "session-b",
            "segment-b",
            AudioSourceKind.SystemOutput,
            "texto del segmento solicitado");

        Assert.True(guard.HasUnsavedDraft);
        Assert.False(blocked.CanNavigate);
        Assert.Equal("session-a", blocked.SessionId);
        Assert.Equal("segment-a", blocked.SegmentId);
        Assert.Equal(AudioSourceKind.Microphone, blocked.Source);
        Assert.Equal("borrador todavía no guardado", blocked.EditorText);
        Assert.Contains("Guardar corrección", CorrectionDraftNavigationGuard.BlockedStatus, StringComparison.Ordinal);
        Assert.Contains("Descartar borrador", CorrectionDraftNavigationGuard.BlockedStatus, StringComparison.Ordinal);

        guard.Clear();
        var allowed = guard.ResolveSegmentTransition(
            "session-b",
            "segment-b",
            AudioSourceKind.SystemOutput,
            "texto del segmento solicitado");

        Assert.True(allowed.CanNavigate);
        Assert.Equal("segment-b", allowed.SegmentId);
        Assert.Equal("texto del segmento solicitado", allowed.EditorText);
    }

    [Fact]
    public void NavigationGuard_ReturningEditorToSavedTextClearsDraft()
    {
        var guard = new CorrectionDraftNavigationGuard();
        guard.SetContext(
            "session",
            "segment",
            AudioSourceKind.SystemOutput,
            "texto guardado");
        guard.ObserveEditor("texto editado");
        Assert.True(guard.HasUnsavedDraft);

        guard.ObserveEditor("texto guardado");

        Assert.False(guard.HasUnsavedDraft);
        Assert.True(guard.CanNavigate());
    }

    [Fact]
    public async Task EditorOperation_EditDuringAwaitRejectsReplacementAndPreservesDraft()
    {
        var guard = new CorrectionDraftNavigationGuard();
        guard.SetContext(
            "session",
            "segment",
            AudioSourceKind.Microphone,
            "texto guardado");
        var ticket = guard.CaptureOperation();
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var operation = guard.RunAsync(
            ticket,
            async token =>
            {
                started.TrySetResult();
                return await release.Task.WaitAsync(token);
            },
            CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        guard.ObserveEditor("edición creada durante la espera");
        release.TrySetResult(42);

        var result = await operation.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(42, result.Value);
        Assert.False(result.CanReplaceEditor);
        var draft = Assert.IsType<CorrectionDraftSnapshot>(guard.Current);
        Assert.Equal("texto guardado", draft.SavedText);
        Assert.Equal("edición creada durante la espera", draft.EditorText);
    }

    [Fact]
    public async Task SaveOperation_FreezesSubmittedTextAndKeepsLaterEditOverSavedBaseline()
    {
        var guard = new CorrectionDraftNavigationGuard();
        guard.SetContext(
            "session",
            "segment",
            AudioSourceKind.SystemOutput,
            "texto original");
        guard.ObserveEditor("texto enviado");
        var save = guard.CaptureSave();
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        string? submittedText = null;
        var operation = guard.RunSaveAsync(
            save,
            async (frozenText, token) =>
            {
                submittedText = frozenText;
                started.TrySetResult();
                await release.Task.WaitAsync(token);
            },
            CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        guard.ObserveEditor("edición posterior");
        release.TrySetResult(true);

        var result = await operation.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("texto enviado", submittedText);
        Assert.Equal("texto enviado", save.FrozenText);
        Assert.Equal("texto enviado", result.FrozenText);
        Assert.False(result.Baseline.CanReplaceEditor);
        Assert.True(result.Baseline.HasUnsavedDraft);
        var draft = Assert.IsType<CorrectionDraftSnapshot>(guard.Current);
        Assert.Equal("texto enviado", draft.SavedText);
        Assert.Equal("edición posterior", draft.EditorText);
    }

    [Fact]
    public async Task QueryDispatcher_ExecutesHeavyQueryOutsideCallerScheduler()
    {
        var schedulerPair = new ConcurrentExclusiveSchedulerPair();
        var callerScheduler = schedulerPair.ExclusiveScheduler;
        TaskScheduler? queryScheduler = null;

        var result = await Task.Factory.StartNew(
                async () => await PendingReviewQueryDispatcher.RunAsync(
                    _ =>
                    {
                        queryScheduler = TaskScheduler.Current;
                        return Task.FromResult(42);
                    },
                    CancellationToken.None),
                CancellationToken.None,
                TaskCreationOptions.None,
                callerScheduler)
            .Unwrap();

        schedulerPair.Complete();
        await schedulerPair.Completion;
        Assert.Equal(42, result);
        Assert.Same(TaskScheduler.Default, queryScheduler);
        Assert.NotSame(callerScheduler, queryScheduler);
    }

    [Fact]
    public async Task QueryDispatcher_PropagatesCancellationToHeavyQuery()
    {
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource<CancellationToken>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = PendingReviewQueryDispatcher.RunAsync(
            async token =>
            {
                started.TrySetResult(token);
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return 1;
            },
            cancellation.Token);

        var observedToken = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.True(observedToken.IsCancellationRequested);
    }

    [Fact]
    public void PendingReviewCoordinator_NewerLoadCancelsAndSupersedesOlderResult()
    {
        using var coordinator = new PendingReviewCoordinator();
        var first = coordinator.Begin();
        var second = coordinator.Begin();

        Assert.True(first.CancellationToken.IsCancellationRequested);
        Assert.False(coordinator.IsCurrent(first));
        Assert.True(coordinator.IsCurrent(second));

        coordinator.Invalidate();
        Assert.True(second.CancellationToken.IsCancellationRequested);
        Assert.False(coordinator.IsCurrent(second));
    }

    [Fact]
    public async Task PendingReviewCoordinator_OverlappedStaleResultCannotPublish()
    {
        using var coordinator = new PendingReviewCoordinator();
        var stale = coordinator.Begin();
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var query = PendingReviewQueryDispatcher.RunAsync(
            async _ =>
            {
                started.TrySetResult();
                return await release.Task;
            },
            stale.CancellationToken);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var current = coordinator.Begin();
        release.TrySetResult(7);
        var value = await query.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(7, value);
        Assert.True(stale.CancellationToken.IsCancellationRequested);
        Assert.False(coordinator.IsCurrent(stale));
        Assert.True(coordinator.IsCurrent(current));
    }

    private static TranscriptSegment Segment() => new(
        "segment",
        "session",
        AudioSourceKind.SystemOutput,
        0,
        TimeSpan.Zero,
        TimeSpan.FromSeconds(1),
        "original",
        StartedAt);
}
