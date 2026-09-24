using Trazio.AsistenteReunion.App;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.Tests;

public sealed class AsyncOperationCoordinationTests
{
    [Fact]
    public void HistorySearchCoordinator_NewerQueryCancelsAndSupersedesOlderResults()
    {
        using var coordinator = new HistorySearchCoordinator();
        var first = coordinator.Begin("primera");
        var second = coordinator.Begin("segunda");

        Assert.True(first.CancellationToken.IsCancellationRequested);
        Assert.False(coordinator.IsCurrent(first, "primera"));
        Assert.True(coordinator.IsCurrent(second, " segunda "));

        coordinator.Invalidate();
        Assert.True(second.CancellationToken.IsCancellationRequested);
        Assert.False(coordinator.IsCurrent(second, "segunda"));
    }

    [Fact]
    public void HistorySearchNavigationCoordinator_NewerSelectionCancelsAndSupersedesOlderNavigation()
    {
        using var coordinator = new HistorySearchNavigationCoordinator();
        var firstKey = new HistorySearchNavigationKey("session-a", "segment-a", AudioSourceKind.Microphone);
        var secondKey = new HistorySearchNavigationKey("session-b", "segment-b", AudioSourceKind.SystemOutput);
        var first = coordinator.Begin(firstKey);
        var second = coordinator.Begin(secondKey);

        Assert.True(first.CancellationToken.IsCancellationRequested);
        Assert.False(coordinator.IsCurrent(first, firstKey));
        Assert.False(coordinator.IsCurrent(second, firstKey));
        Assert.True(coordinator.IsCurrent(second, secondKey));

        coordinator.Invalidate();
        Assert.True(second.CancellationToken.IsCancellationRequested);
        Assert.False(coordinator.IsCurrent(second, secondKey));
    }

    [Fact]
    public async Task HistorySearchActivityGate_CloseBlocksNewWorkAndWaitsForSearchAndNavigation()
    {
        var gate = new HistorySearchActivityGate();
        Assert.True(gate.TryBegin(out var search));
        Assert.True(gate.TryBegin(out var navigation));

        var closing = gate.BlockAndDrainAsync();

        Assert.False(closing.IsCompleted);
        Assert.False(gate.IsAccepting);
        Assert.False(gate.TryBegin(out _));
        search.Dispose();
        Assert.False(closing.IsCompleted);
        navigation.Dispose();
        await closing;
        Assert.False(gate.TryBegin(out _));
    }

    [Fact]
    public async Task HistorySearchActivityGate_DeleteCanReopenOnlyAfterActiveWorkDrains()
    {
        var gate = new HistorySearchActivityGate();
        Assert.True(gate.TryBegin(out var navigation));

        var deleting = gate.BlockAndDrainAsync();
        Assert.False(gate.TryBegin(out _));
        navigation.Dispose();
        await deleting;

        gate.Reopen();
        Assert.True(gate.IsAccepting);
        Assert.True(gate.TryBegin(out var nextSearch));
        nextSearch.Dispose();
    }

    [Fact]
    public void HistorySelectionCoordinator_ParentNavigationCancellationInvalidatesLinkedLoad()
    {
        using var navigation = new CancellationTokenSource();
        using var coordinator = new HistorySelectionCoordinator();
        var load = coordinator.Begin("session", navigation.Token);

        navigation.Cancel();

        Assert.True(load.CancellationToken.IsCancellationRequested);
        Assert.False(coordinator.IsCurrent(load, "session"));
    }

    [Fact]
    public void HistorySelectionCoordinator_NewSelectionInvalidatesOldTicket()
    {
        using var coordinator = new HistorySelectionCoordinator();
        var first = coordinator.Begin("session-a");
        var second = coordinator.Begin("session-b");

        Assert.True(first.CancellationToken.IsCancellationRequested);
        Assert.False(coordinator.IsCurrent(first, "session-a"));
        Assert.True(coordinator.IsCurrent(second, "session-b"));
        Assert.Throws<OperationCanceledException>(() => coordinator.Capture("session-a"));
    }

    [Fact]
    public void HistorySelectionCoordinator_SourceReloadSupersedesPriorResultForSameSession()
    {
        using var coordinator = new HistorySelectionCoordinator();
        var first = coordinator.Begin("session");
        var reload = coordinator.Begin("session");

        Assert.False(coordinator.IsCurrent(first, "session"));
        Assert.True(coordinator.IsCurrent(reload, "session"));
    }

    [Fact]
    public async Task AddGlossary_WhenMeetingChangesWhilePending_CancelsAndCannotPublishStaleResult()
    {
        using var coordinator = new HistorySelectionCoordinator();
        var glossaryTicket = coordinator.Begin("meeting-a");
        var insertStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingInsert = Task.Run(async () =>
        {
            insertStarted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, glossaryTicket.CancellationToken);
        });
        await insertStarted.Task;

        var visibleMeeting = coordinator.Begin("meeting-b");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pendingInsert);
        Assert.False(coordinator.IsCurrent(glossaryTicket, "meeting-b"));
        Assert.True(coordinator.IsCurrent(visibleMeeting, "meeting-b"));
    }

    [Fact]
    public async Task RevisionSelectionCoordinator_ReorderedCompletionsOnlyPublishLatestSelection()
    {
        using var coordinator = new RevisionSelectionCoordinator();
        var first = coordinator.Begin("session", AudioSourceKind.Microphone, "revision-a");
        var firstCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstLoad = Task.Run(async () =>
        {
            await firstCompletion.Task;
            return coordinator.IsCurrent(first, "session", AudioSourceKind.Microphone, "revision-a");
        });

        var second = coordinator.Begin("session", AudioSourceKind.Microphone, "revision-b");
        var secondCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondLoad = Task.Run(async () =>
        {
            await secondCompletion.Task;
            return coordinator.IsCurrent(second, "session", AudioSourceKind.Microphone, "revision-b");
        });

        secondCompletion.SetResult();
        Assert.True(await secondLoad);
        firstCompletion.SetResult();
        Assert.False(await firstLoad);
        Assert.True(first.CancellationToken.IsCancellationRequested);
    }
    [Fact]
    public void PlaybackOperationCoordinator_OnlyLatestOperationCanCompleteUiState()
    {
        var coordinator = new PlaybackOperationCoordinator();
        var first = coordinator.Begin();
        var second = coordinator.Begin();

        Assert.False(coordinator.IsCurrent(first));
        Assert.True(coordinator.IsCurrent(second));
        coordinator.Supersede();
        Assert.False(coordinator.IsCurrent(second));
    }

    [Fact]
    public void PlaybackDurationBudget_PausedPositionDoesNotConsumeDuration()
    {
        var budget = new PlaybackDurationBudget(TimeSpan.FromSeconds(8));
        budget.StartChunk(TimeSpan.FromSeconds(3));
        budget.Observe(TimeSpan.FromSeconds(5));
        var beforePause = budget.Remaining;

        budget.Observe(TimeSpan.FromSeconds(5));
        budget.Observe(TimeSpan.FromSeconds(5));
        budget.Observe(TimeSpan.FromSeconds(8));

        Assert.Equal(TimeSpan.FromSeconds(6), beforePause);
        Assert.Equal(TimeSpan.FromSeconds(3), budget.Remaining);
        Assert.False(budget.IsExhausted);
    }

    [Fact]
    public async Task OwnedCancellationOperation_SecondStartRejectedAndClosingWaitsForOwnerCompletion()
    {
        using var coordinator = new OwnedCancellationOperationCoordinator();
        Assert.True(coordinator.TryBegin([CancellationToken.None], out var owner));
        Assert.False(coordinator.TryBegin([CancellationToken.None], out _));
        var closing = coordinator.CancelAndWaitAsync();
        Assert.True(owner.CancellationToken.IsCancellationRequested);
        Assert.False(closing.IsCompleted);

        Assert.True(coordinator.Complete(owner));
        await closing;
        Assert.False(coordinator.IsRunning);
        Assert.False(coordinator.Complete(owner));
    }}

