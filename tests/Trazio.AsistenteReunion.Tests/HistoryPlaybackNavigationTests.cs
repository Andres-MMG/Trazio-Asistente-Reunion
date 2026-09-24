using Trazio.AsistenteReunion.App;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.Tests;

public sealed class HistoryPlaybackNavigationTests
{
    private static readonly IReadOnlyList<string> VisibleIds = ["first", "middle", "last"];

    [Fact]
    public void Create_FirstRow_OnlyEnablesNext()
    {
        var state = HistoryPlaybackNavigation.Create(VisibleIds, "first");

        Assert.False(state.CanPrevious);
        Assert.True(state.CanNext);
        Assert.Null(state.PreviousId);
        Assert.Equal("middle", state.NextId);
    }

    [Fact]
    public void Create_MiddleRow_ExposesBothNeighbors()
    {
        var state = HistoryPlaybackNavigation.Create(VisibleIds, "middle");

        Assert.True(state.CanPrevious);
        Assert.True(state.CanNext);
        Assert.Equal("first", state.PreviousId);
        Assert.Equal("last", state.NextId);
    }

    [Fact]
    public void Create_LastRow_OnlyEnablesPrevious()
    {
        var state = HistoryPlaybackNavigation.Create(VisibleIds, "last");

        Assert.True(state.CanPrevious);
        Assert.False(state.CanNext);
        Assert.Equal("middle", state.PreviousId);
        Assert.Null(state.NextId);
    }

    [Fact]
    public void Create_NoSelectionEmptyOrSingleRow_IsSafe()
    {
        Assert.Equal(HistoryPlaybackNavigationState.Empty, HistoryPlaybackNavigation.Create([], null));
        Assert.Equal(HistoryPlaybackNavigationState.Empty, HistoryPlaybackNavigation.Create(["only"], null));

        var selectedOnly = HistoryPlaybackNavigation.Create(["only"], "only");
        Assert.False(selectedOnly.CanPrevious);
        Assert.False(selectedOnly.CanNext);
    }

    [Fact]
    public void Create_SelectedRowMissingFromReplacementList_DisablesNavigation()
    {
        var state = HistoryPlaybackNavigation.Create(["replacement-a", "replacement-b"], "old-row");

        Assert.Equal(HistoryPlaybackNavigationState.Empty, state);
    }

    [Fact]
    public void Create_PreservesVisibleOrderAcrossMixedSources()
    {
        var visible = new[]
        {
            Segment("mic", AudioSourceKind.Microphone, 20),
            Segment("system", AudioSourceKind.SystemOutput, 5),
            Segment("mic-later", AudioSourceKind.Microphone, 30)
        };

        var state = HistoryPlaybackNavigation.Create(visible.Select(item => item.Id).ToArray(), "system");

        Assert.Equal("mic", state.PreviousId);
        Assert.Equal("mic-later", state.NextId);
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    public void PlaybackAvailability_FailsClosedDuringDeleteOrClose(
        bool deleteInProgress,
        bool closing,
        bool expected)
    {
        Assert.Equal(expected, HistoryPlaybackAvailability.CanBeginOperation(deleteInProgress, closing));
    }

    [Fact]
    public async Task PlaybackTransitionGate_SerializesStopAndStartTransitions()
    {
        var gate = new PlaybackTransitionGate();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = gate.RunAsync(async () =>
        {
            firstEntered.TrySetResult();
            await releaseFirst.Task;
        });
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var second = gate.RunAsync(() =>
        {
            secondEntered.TrySetResult();
            return Task.CompletedTask;
        });
        Assert.False(secondEntered.Task.IsCompleted);

        releaseFirst.TrySetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(secondEntered.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public void PlaybackTransitionEpoch_StopGenerationRejectsLatePublicationAfterNewStart()
    {
        var epoch = new PlaybackTransitionEpoch();
        var stoppedGeneration = epoch.Advance();

        Assert.True(epoch.IsCurrent(stoppedGeneration));

        var newPlaybackGeneration = epoch.Advance();

        Assert.False(epoch.IsCurrent(stoppedGeneration));
        Assert.True(epoch.IsCurrent(newPlaybackGeneration));
    }

    [Fact]
    public void Highlight_UsesSourceAndEndExclusiveIntervals()
    {
        var rows = new[]
        {
            Segment("microphone", AudioSourceKind.Microphone, 0, 10),
            Segment("system", AudioSourceKind.SystemOutput, 0, 10),
            Segment("system-next", AudioSourceKind.SystemOutput, 10, 20)
        };

        Assert.Equal("system", HistoryPlaybackHighlight.ResolveSegmentId(rows, AudioSourceKind.SystemOutput, TimeSpan.Zero));
        Assert.Equal("system-next", HistoryPlaybackHighlight.ResolveSegmentId(rows, AudioSourceKind.SystemOutput, TimeSpan.FromSeconds(10)));
        Assert.Null(HistoryPlaybackHighlight.ResolveSegmentId(rows, AudioSourceKind.SystemOutput, TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public void Highlight_GapReturnsNoneAndOverlapUsesLatestStartThenSequence()
    {
        var rows = new[]
        {
            Segment("wide", AudioSourceKind.SystemOutput, 0, 20, sequence: 0),
            Segment("latest-low", AudioSourceKind.SystemOutput, 5, 15, sequence: 1),
            Segment("latest-high", AudioSourceKind.SystemOutput, 5, 15, sequence: 2),
            Segment("after-gap", AudioSourceKind.SystemOutput, 30, 40, sequence: 3)
        };

        Assert.Equal("latest-high", HistoryPlaybackHighlight.ResolveSegmentId(rows, AudioSourceKind.SystemOutput, TimeSpan.FromSeconds(8)));
        Assert.Null(HistoryPlaybackHighlight.ResolveSegmentId(rows, AudioSourceKind.SystemOutput, TimeSpan.FromSeconds(25)));
    }

    private static TranscriptSegment Segment(
        string id,
        AudioSourceKind source,
        int start,
        int end = 40,
        long sequence = 0) =>
        new(id, "session", source, sequence, TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), id, DateTimeOffset.UnixEpoch);
}
