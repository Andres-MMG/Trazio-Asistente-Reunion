using Trazio.AsistenteReunion.App;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.Tests;

public sealed class HistoryRetranscriptionPresenterTests
{
    [Theory]
    [InlineData(SessionState.Completed)]
    [InlineData(SessionState.Interrupted)]
    public void Create_RetainedAudioAndTerminalSession_EnablesStartWithoutPriorTranscript(SessionState state)
    {
        var result = HistoryRetranscriptionPresenter.Create(state, hasSelectedSourceAudio: true, isRunning: false);
        Assert.True(result.CanStart);
        Assert.False(result.CanCancel);
    }

    [Theory]
    [InlineData(SessionState.Recording)]
    [InlineData(SessionState.Paused)]
    public void Create_ActiveSession_DisablesRetranscriptionWithGuidance(SessionState state)
    {
        var result = HistoryRetranscriptionPresenter.Create(state, true, false);
        Assert.False(result.CanStart);
        Assert.Contains("Detén", result.Guidance);
    }

    [Fact]
    public void Create_NoRetainedAudio_DisablesRetranscriptionAndExplainsPruning()
    {
        var result = HistoryRetranscriptionPresenter.Create(SessionState.Completed, false, false);
        Assert.False(result.CanStart);
        Assert.Contains("eliminado", result.Guidance, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Create_RunningOperation_EnablesOnlyCancel()
    {
        var result = HistoryRetranscriptionPresenter.Create(SessionState.Completed, true, true);
        Assert.False(result.CanStart);
        Assert.True(result.CanCancel);
    }
}
