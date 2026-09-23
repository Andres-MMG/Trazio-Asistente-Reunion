using Trazio.AsistenteReunion.App;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.Tests;

public sealed class HistoryPresenterTests
{
    [Fact]
    public void Create_WithoutSelection_DisablesEverySessionAction()
    {
        var state = HistoryPresenter.Create(false, null, [], [], AudioSourceKind.Microphone, _ => "unused");

        Assert.False(state.CanPlayAudio);
        Assert.False(state.CanExportWav);
        Assert.False(state.CanExportTxt);
        Assert.False(state.CanExportMarkdown);
        Assert.False(state.CanDelete);
        Assert.False(state.CanChooseSource);
        Assert.Equal(HistoryPresenter.SelectSessionMessage, state.TranscriptContent);
    }

    [Fact]
    public void Create_WithTranscriptAndRequestedAudio_EnablesRelevantActions()
    {
        var segments = new[] { Segment(AudioSourceKind.Microphone) };
        var audio = new[] { Summary(AudioSourceKind.Microphone) };

        var state = HistoryPresenter.Create(true, SessionState.Completed, segments, audio, AudioSourceKind.Microphone, _ => "formatted");

        Assert.True(state.CanPlayAudio);
        Assert.True(state.CanExportWav);
        Assert.True(state.CanExportTxt);
        Assert.True(state.CanExportMarkdown);
        Assert.True(state.CanDelete);
        Assert.True(state.CanChooseSource);
        Assert.Equal("formatted", state.TranscriptContent);
    }

    [Fact]
    public void Create_WithOnlyOtherSource_FallsBackToAvailableSource()
    {
        var state = HistoryPresenter.Create(true, SessionState.Completed, [], [Summary(AudioSourceKind.SystemOutput)], AudioSourceKind.Microphone, _ => "unused");

        Assert.Equal(AudioSourceKind.SystemOutput, state.SelectedSource);
        Assert.True(state.CanPlayAudio);
        Assert.True(state.CanExportWav);
        Assert.False(state.CanExportTxt);
        Assert.False(state.CanExportMarkdown);
        Assert.Equal(HistoryPresenter.NoTranscriptMessage, state.TranscriptContent);
    }

    [Fact]
    public void Create_WithNoRetainedAudio_ExplainsRetentionOrPruning()
    {
        var state = HistoryPresenter.Create(true, SessionState.Completed, [Segment(AudioSourceKind.Microphone)], [], AudioSourceKind.Microphone, _ => "formatted");

        Assert.False(state.CanChooseSource);
        Assert.False(state.CanPlayAudio);
        Assert.Contains("no guardó audio", state.AudioGuidance, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("eliminó", state.AudioGuidance, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SessionState.Recording, false)]
    [InlineData(SessionState.Paused, false)]
    [InlineData(SessionState.Completed, true)]
    [InlineData(SessionState.Interrupted, true)]
    public void Create_GatesDeletionBySessionLifecycle(SessionState sessionState, bool expectedCanDelete)
    {
        var state = HistoryPresenter.Create(true, sessionState, [], [], AudioSourceKind.Microphone, _ => "unused");

        Assert.Equal(expectedCanDelete, state.CanDelete);
        Assert.Equal(!expectedCanDelete, state.IsActiveSession);
        if (!expectedCanDelete)
        {
            Assert.Contains("Detenla antes de eliminarla", state.DeleteReason);
            Assert.Contains("Detenla antes de eliminarla", state.ActionGuidance);
        }
    }

    [Fact]
    public void HistorySessionItem_From_UsesRequestedLocalTimezoneAndState()
    {
        var timezone = TimeZoneInfo.CreateCustomTimeZone("Test", TimeSpan.FromHours(-3), "Test", "Test");
        var session = new SessionSummary("id", "a", new DateTimeOffset(2026, 9, 21, 15, 30, 0, TimeSpan.Zero), null, SessionState.Interrupted);

        var item = HistorySessionItem.From(session, timezone);

        Assert.Equal("a", item.Title);
        Assert.Equal("2026-09-21 12:30 · Interrumpida", item.Details);
        Assert.Same(session, item.Session);
    }

    [Fact]
    public void StorageLocationFacts_Current_DescribesEncryptedStorageAndPlaintextExports()
    {
        var facts = StorageLocationFacts.Current();

        Assert.Equal(ApplicationPaths.DataDirectory, facts.DataDirectory);
        Assert.Equal(ApplicationPaths.DatabasePath, facts.DatabasePath);
        Assert.Equal(ApplicationPaths.AudioDirectory, facts.AudioDirectory);
        Assert.Contains("cifradas dentro de trazio-transcripts.db", facts.Explanation);
        Assert.Contains("cifrado en la carpeta audio", facts.Explanation);
        Assert.Contains("no están cifradas", facts.Explanation);
    }

    [Fact]
    public void StorageLocationPresenter_WithPendingMove_ShowsRestartGuidanceAndOnlyAllowsCancel()
    {
        var current = Path.Combine(Path.GetTempPath(), "trazio-current");
        var target = Path.Combine(Path.GetTempPath(), "trazio-target");
        var pending = new StorageMigrationIntent("id", current, target);

        var state = StorageLocationPresenter.Create(current, ApplicationPaths.DefaultDataDirectory, new(current, pending, null), true, false, false, false, false);

        Assert.Contains(target, state.Status);
        Assert.Contains("Debes reiniciar", state.Status);
        Assert.False(state.CanChange);
        Assert.False(state.CanRestoreDefault);
        Assert.True(state.CanCancelPending);
    }

    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, true)]
    public void StorageLocationPresenter_WhenOperationUnsafe_DisablesRelocation(bool recording, bool playing, bool busy, bool closing)
    {
        var current = Path.Combine(Path.GetTempPath(), "trazio-custom");
        var state = StorageLocationPresenter.Create(current, ApplicationPaths.DefaultDataDirectory, new(current, null, null), true, recording, playing, busy, closing);

        Assert.False(state.CanChange);
        Assert.False(state.CanRestoreDefault);
        Assert.False(state.CanCancelPending);
    }

    [Fact]
    public void StorageLocationPresenter_AtDefault_DoesNotOfferRedundantRestore()
    {
        var state = StorageLocationPresenter.Create(ApplicationPaths.DefaultDataDirectory, ApplicationPaths.DefaultDataDirectory, new(null, null, null), true, false, false, false, false);

        Assert.True(state.CanChange);
        Assert.False(state.CanRestoreDefault);
        Assert.Contains("traslado verificado al reiniciar", state.Status);
    }
    [Fact]
    public void StorageLocationPresenter_WithCommittedCleanup_ShowsResumeStateAndDisablesCancel()
    {
        var current = Path.Combine(Path.GetTempPath(), "trazio-active-target");
        var source = Path.Combine(Path.GetTempPath(), "trazio-old-source");
        var pending = new StorageMigrationIntent("id", source, current, []);

        var state = StorageLocationPresenter.Create(
            current,
            ApplicationPaths.DefaultDataDirectory,
            new(current, pending, source),
            true,
            false,
            false,
            false,
            false);

        Assert.Contains("limpieza verificada de la carpeta anterior está pendiente", state.Status, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ya no se puede cancelar", state.Status, StringComparison.OrdinalIgnoreCase);
        Assert.False(state.CanChange);
        Assert.False(state.CanRestoreDefault);
        Assert.False(state.CanCancelPending);
    }
    private static TranscriptSegment Segment(AudioSourceKind source) =>
        new("segment", "session", source, 0, TimeSpan.Zero, TimeSpan.FromSeconds(1), "hello", DateTimeOffset.UtcNow);

    private static AudioArchiveSummary Summary(AudioSourceKind source) =>
        new(source, 1, TimeSpan.FromSeconds(30), 100);
}



