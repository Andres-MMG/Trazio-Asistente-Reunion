using System.Reflection;
using System.Text;
using Trazio.AsistenteReunion.App;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.Tests;

public sealed class HistoryWorkspacePublicationTests
{
    [Fact]
    public void SelectingSavedSession_CompiledApplicationContainsCompleteReviewWorkspace()
    {
        var state = HistoryPresenter.Create(
            true,
            SessionState.Completed,
            [new TranscriptSegment("segment", "session", AudioSourceKind.Microphone, 0, TimeSpan.Zero, TimeSpan.FromSeconds(1), "text", DateTimeOffset.UtcNow)],
            [],
            AudioSourceKind.Microphone,
            _ => "formatted");
        var compiledApplication = Encoding.UTF8.GetString(File.ReadAllBytes(typeof(MainWindow).Assembly.Location));

        Assert.True(state.HasSelection);
        Assert.True(state.CanCorrectTranscript);
        Assert.Equal("formatted", state.TranscriptContent);
        Assert.Contains("Detalles de la sesión", compiledApplication);
        Assert.Contains("Revisar segmento seleccionado", compiledApplication);
        Assert.Contains("Reemplazos de palabras detectados", compiledApplication);
        Assert.Contains("Agregar términos seleccionados", compiledApplication);
        Assert.Contains("Almacenamiento local", compiledApplication);
        Assert.Contains("Original · revisión humana", compiledApplication);
        Assert.Contains("Retranscribir audio", compiledApplication);
        Assert.Contains("Cancelar retranscripción", compiledApplication);
        Assert.Contains("Tu nombre en esta reunión (opcional)", compiledApplication);
        Assert.Contains("Guardar título", compiledApplication);
        Assert.Contains("Comparar transcripciones", compiledApplication);
        Assert.Contains("Primera versión", compiledApplication);
        Assert.Contains("Segunda versión", compiledApplication);
        Assert.Contains("Reproductor de la reunión", compiledApplication);
        Assert.Contains("Micrófono · mi voz", compiledApplication);
        Assert.Contains("Audio del equipo · participantes", compiledApplication);
        Assert.Contains("Escuchar fragmento", compiledApplication);
        Assert.Contains("Retroceder 10 s", compiledApplication);
        Assert.Contains("Avanzar 10 s", compiledApplication);
        Assert.Contains("Segmento anterior", compiledApplication);
        Assert.Contains("Segmento siguiente", compiledApplication);
        Assert.Contains("Exportar a Obsidian…", compiledApplication);
        Assert.Contains("Aplicación de reunión (opcional)", compiledApplication);
        Assert.Contains("Asociar una ventana solo identifica la aplicación: no inicia la captura visual ni el análisis anónimo", compiledApplication);
        Assert.Contains("Seleccionar ventana de reunión", compiledApplication);
        Assert.Contains("No se pudo consultar la lista de ventanas. Intenta nuevamente.", compiledApplication);
        Assert.DoesNotContain("No se pudo consultar la lista de ventanas:", compiledApplication, StringComparison.Ordinal);
        Assert.NotNull(typeof(MeetingWindowSelectionController).GetMethod(nameof(MeetingWindowSelectionController.FinishSession)));
        Assert.Null(typeof(MeetingWindowSelection).GetProperty("TransientTitle"));
        Assert.Null(typeof(MeetingWindowSelection).GetProperty("TransientProcessName"));
        Assert.Equal(typeof(MeetingWindowSelection), typeof(MeetingWindowPickerWindow).GetProperty(nameof(MeetingWindowPickerWindow.SelectedSelection))?.PropertyType);
        Assert.Null(typeof(MeetingWindowPickerWindow).GetProperty("SelectedWindow"));
        Assert.NotNull(typeof(MainWindow).GetField("SelectMeetingWindowButton", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
        Assert.NotNull(typeof(MainWindow).GetMethod("SelectMeetingWindow_Click", BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.NotNull(typeof(MainWindow).GetMethod("ClearMeetingWindow_Click", BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.NotNull(typeof(MainWindow).GetField("ExportObsidianButton", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
        Assert.NotNull(typeof(MainWindow).GetMethod("ExportObsidian_Click", BindingFlags.Instance | BindingFlags.NonPublic));
    }

    [Fact]
    public void HistoryPlaybackNavigation_XamlAndHandlersAreAccessibleAndNeverAutoplayOnSelection()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(
            root, "src", "Trazio.AsistenteReunion.App", "MainWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(
            root, "src", "Trazio.AsistenteReunion.App", "MainWindow.xaml.cs"));

        Assert.Contains("x:Name=\"PreviousHistorySegmentButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"NextHistorySegmentButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"PreviousHistorySegment_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"NextHistorySegment_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Ir al segmento anterior\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Ir al segmento siguiente\"", xaml, StringComparison.Ordinal);
        Assert.Contains("sin reproducir audio automáticamente", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"PlaybackStatusText\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Estado de reproducción\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.LiveSetting=\"Polite\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Binding IsPlaybackActive", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding PlaybackAnnouncement}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"{Binding PlaybackAnnouncement}\"", xaml, StringComparison.Ordinal);

        var navigationStart = code.IndexOf("private void NavigateHistorySegment", StringComparison.Ordinal);
        var navigationEnd = code.IndexOf("private HistoryPlaybackNavigationState CurrentHistoryNavigation", navigationStart, StringComparison.Ordinal);
        var navigationHandler = code[navigationStart..navigationEnd];
        Assert.DoesNotContain("PlaySelectedSegmentAsync", navigationHandler, StringComparison.Ordinal);
        Assert.DoesNotContain("PlayFromPositionAsync", navigationHandler, StringComparison.Ordinal);

        var selectionStart = code.IndexOf("private void HistorySegments_SelectionChanged", StringComparison.Ordinal);
        var selectionEnd = code.IndexOf("private void PreviousHistorySegment_Click", selectionStart, StringComparison.Ordinal);
        var selectionHandler = code[selectionStart..selectionEnd];
        Assert.DoesNotContain("PlaySelectedSegmentAsync", selectionHandler, StringComparison.Ordinal);
        Assert.DoesNotContain("PlayFromPositionAsync", selectionHandler, StringComparison.Ordinal);

        var seekStart = code.IndexOf("private async Task SeekToAsync", StringComparison.Ordinal);
        var seekEnd = code.IndexOf("private async Task PlayFromPositionAsync", seekStart, StringComparison.Ordinal);
        var seekHandler = code[seekStart..seekEnd];
        Assert.Contains("ResolvePlayablePosition(session, requested)", seekHandler, StringComparison.Ordinal);
        Assert.Contains("se avanzó a", seekHandler, StringComparison.Ordinal);
    }

    [Fact]
    public void HistoryPlaybackLifecycle_UsesTransientSegmentSourceAndAwaitsCancellationBeforeDelete()
    {
        var root = FindRepositoryRoot();
        var code = File.ReadAllText(Path.Combine(
            root, "src", "Trazio.AsistenteReunion.App", "MainWindow.xaml.cs"));
        var playbackService = File.ReadAllText(Path.Combine(
            root, "src", "Trazio.AsistenteReunion.App", "AudioPlaybackService.cs"));

        var segmentStart = code.IndexOf("private async Task PlaySelectedSegmentAsync", StringComparison.Ordinal);
        var segmentEnd = code.IndexOf("private async void SaveCorrection_Click", segmentStart, StringComparison.Ordinal);
        var segmentHandler = code[segmentStart..segmentEnd];
        Assert.Contains("CreatePlaybackTrack(session, source, chunks)", segmentHandler, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectHistorySource", segmentHandler, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplyHistoryTrack", segmentHandler, StringComparison.Ordinal);
        Assert.DoesNotContain("HistoryRevisionSelector.SelectedItem", segmentHandler, StringComparison.Ordinal);

        Assert.Contains("_activePlaybackSource ?? _historyTrackSource", code, StringComparison.Ordinal);
        Assert.Contains("await Task.WhenAll(_playback.StopAsync(), operationCompletion)", code, StringComparison.Ordinal);
        Assert.Contains("_activePlaybackOperation is not null || _historyPlaying", code, StringComparison.Ordinal);

        Assert.Contains("allowSeeking: false", segmentHandler, StringComparison.Ordinal);
        Assert.DoesNotContain("HistoryAudioSource.SelectedItem", segmentHandler, StringComparison.Ordinal);
        Assert.Contains("_historyPlaying && !_activePlaybackAllowsSeeking", code, StringComparison.Ordinal);
        Assert.Contains("_historyPlaying && _activePlaybackOperation is not null", code, StringComparison.Ordinal);
        Assert.Contains("PlaybackStatusText.Text = next is null", code, StringComparison.Ordinal);
        Assert.Contains("$\"Reproduciendo {next.Header}.\"", code, StringComparison.Ordinal);
        Assert.Contains("PlaybackStatusText.Text = \"Preparando reproducción de audio.\"", code, StringComparison.Ordinal);
        Assert.Contains("PlaybackStatusText.Text = \"Audio no disponible para el fragmento seleccionado.\"", segmentHandler, StringComparison.Ordinal);

        var beginStart = code.IndexOf("private Task<PlaybackOperationRun?> BeginPlaybackOperationAsync", StringComparison.Ordinal);
        var beginEnd = code.IndexOf("private bool IsCurrentPlayback", beginStart, StringComparison.Ordinal);
        var beginHandler = code[beginStart..beginEnd];
        Assert.Equal(2, CountOccurrences(beginHandler, "HistoryPlaybackAvailability.CanBeginOperation"));
        Assert.Contains("_playbackTransitions.RunAsync", beginHandler, StringComparison.Ordinal);
        Assert.Contains("SupersedePlaybackCoreAsync", beginHandler, StringComparison.Ordinal);
        Assert.Contains("_playbackTransitions.RunAsync(() => SupersedePlaybackCoreAsync(announce))", code, StringComparison.Ordinal);
        Assert.Contains("if (!_playbackTransitionEpoch.IsCurrent(stopGeneration)) return", code, StringComparison.Ordinal);
        Assert.Contains("Func<bool> canPublish", code, StringComparison.Ordinal);
        Assert.Contains("if (!canPublish()) return false", code, StringComparison.Ordinal);
        Assert.Contains("var trackPublished = await ApplyHistoryTrackAsync", code, StringComparison.Ordinal);
        Assert.Contains("HistorySegments.IsEnabled = !playbackBlocked", code, StringComparison.Ordinal);
        Assert.Contains("var control = _historyPlaybackPaused ? _playback.Resume() : _playback.Pause()", code, StringComparison.Ordinal);
        Assert.Contains("if (!control.Succeeded)", code, StringComparison.Ordinal);

        var deleteStart = code.IndexOf("private async void Delete_Click", StringComparison.Ordinal);
        var deleteEnd = code.IndexOf("private async void ChangeStorageFolder_Click", deleteStart, StringComparison.Ordinal);
        var deleteHandler = code[deleteStart..deleteEnd];
        Assert.Contains("await SupersedePlaybackAsync(announce: false)", deleteHandler, StringComparison.Ordinal);
        Assert.Contains("_historyLoads.Invalidate()", deleteHandler, StringComparison.Ordinal);
        Assert.Contains("_historyRevisionLoads.Invalidate()", deleteHandler, StringComparison.Ordinal);
        Assert.True(
            deleteHandler.IndexOf("await SupersedePlaybackAsync", StringComparison.Ordinal) <
            deleteHandler.IndexOf("DeleteSessionAsync", StringComparison.Ordinal));

        var closingStart = code.IndexOf("private async void Window_Closing", StringComparison.Ordinal);
        var closingEnd = code.IndexOf("private AppSettings ReadSettings", closingStart, StringComparison.Ordinal);
        var closingHandler = code[closingStart..closingEnd];
        Assert.Contains("_historyLoads.Invalidate()", closingHandler, StringComparison.Ordinal);
        Assert.Contains("_historyRevisionLoads.Invalidate()", closingHandler, StringComparison.Ordinal);
        Assert.True(
            closingHandler.IndexOf("_historyLoads.Invalidate()", StringComparison.Ordinal) <
            closingHandler.IndexOf("await SupersedePlaybackAsync", StringComparison.Ordinal));

        Assert.Contains("private Task _activeTask = Task.CompletedTask", playbackService, StringComparison.Ordinal);
        Assert.Contains("output.GetPosition()", playbackService, StringComparison.Ordinal);
        Assert.DoesNotContain("reader.CurrentTime -", playbackService, StringComparison.Ordinal);
        Assert.Contains("public PlaybackControlResult Pause()", playbackService, StringComparison.Ordinal);
        Assert.Contains("public PlaybackControlResult Resume()", playbackService, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string value, string needle)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(needle, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += needle.Length;
        }
        return count;
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void LiveSession_CompiledApplicationContainsSeparateAccessibleVisualConsentsAndControls()
    {
        var compiledApplication = Encoding.UTF8.GetString(File.ReadAllBytes(typeof(MainWindow).Assembly.Location));
        var root = FindRepositoryRoot();
        var captureConsentXaml = File.ReadAllText(Path.Combine(
            root, "src", "Trazio.AsistenteReunion.App", "VisualCaptureConsentWindow.xaml"));
        var captureConsentCode = File.ReadAllText(Path.Combine(
            root, "src", "Trazio.AsistenteReunion.App", "VisualCaptureConsentWindow.xaml.cs"));
        var analysisConsentXaml = File.ReadAllText(Path.Combine(
            root, "src", "Trazio.AsistenteReunion.App", "AnonymousVisualAnalysisConsentWindow.xaml"));
        var analysisConsentCode = File.ReadAllText(Path.Combine(
            root, "src", "Trazio.AsistenteReunion.App", "AnonymousVisualAnalysisConsentWindow.xaml.cs"));
        var mainXaml = File.ReadAllText(Path.Combine(
            root, "src", "Trazio.AsistenteReunion.App", "MainWindow.xaml"));
        var mainCode = File.ReadAllText(Path.Combine(
            root, "src", "Trazio.AsistenteReunion.App", "MainWindow.xaml.cs"));
        var presentationCode = File.ReadAllText(Path.Combine(
            root, "src", "Trazio.AsistenteReunion.App", "VisualCapturePresentation.cs"));

        Assert.Contains("Autorizar captura visual", compiledApplication);
        Assert.Contains("Autorizar análisis anónimo", compiledApplication);
        Assert.Contains("Captura visual activa · no se guardan imágenes", compiledApplication);
        Assert.Contains("Evidencia visual no disponible: perfil en validación", presentationCode);
        Assert.Contains("No grabará video ni guardará imágenes", compiledApplication);
        Assert.Contains("normalmente 1 vez por segundo y nunca más de 2", compiledApplication);
        Assert.Contains("no lee nombres, chat, subtítulos ni documentos", compiledApplication);
        Assert.Contains("intervalos derivados de actividad y disponibilidad", compiledApplication);
        Assert.Contains("todavía no se exportan", compiledApplication);
        Assert.Contains("audio y la transcripción continuarán", compiledApplication);
        Assert.Contains("La autorización no se guarda ni se reutiliza en otra reunión", compiledApplication);
        Assert.NotNull(typeof(MainWindow).GetField("AuthorizeVisualCaptureButton", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
        Assert.NotNull(typeof(MainWindow).GetField("AuthorizeAnonymousVisualAnalysisButton", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
        Assert.NotNull(typeof(MainWindow).GetField("PauseVisualCaptureButton", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
        Assert.NotNull(typeof(MainWindow).GetField("ResumeVisualCaptureButton", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
        Assert.NotNull(typeof(MainWindow).GetField("StopVisualCaptureButton", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
        Assert.NotNull(typeof(VisualCaptureConsentWindow).GetField("CancelButton", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
        Assert.NotNull(typeof(AnonymousVisualAnalysisConsentWindow).GetField("CancelButton", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
        Assert.Contains("IsCancel=\"True\" IsDefault=\"True\"", captureConsentXaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Cancelar autorización de captura visual\"", captureConsentXaml, StringComparison.Ordinal);
        Assert.Contains("CancelButton.Focus()", captureConsentCode, StringComparison.Ordinal);
        Assert.Contains("IsCancel=\"True\" IsDefault=\"True\"", analysisConsentXaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Cancelar autorización de análisis visual anónimo\"", analysisConsentXaml, StringComparison.Ordinal);
        Assert.Contains("CancelButton.Focus()", analysisConsentCode, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"VisualCaptureStatusText\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"AnonymousVisualAnalysisStatusText\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Captura visual\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Actividad visual anónima\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Autorizar análisis anónimo\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Focusable=\"True\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("VisualCaptureStatusText.Focus()", mainCode, StringComparison.Ordinal);
        Assert.Contains("AnonymousVisualAnalysisStatusText.Focus()", mainCode, StringComparison.Ordinal);
        Assert.Contains("_settings.CaptureSystemOutput", mainCode, StringComparison.Ordinal);
        Assert.Contains("ActiveSessionTimelineContext", mainCode, StringComparison.Ordinal);
        Assert.Contains("new WindowsGraphicsCaptureService(selection, activation.Session)", mainCode, StringComparison.Ordinal);
        Assert.Equal(2, mainCode.Split("VisualCaptureShutdown.RunVisualFirstAsync", StringSplitOptions.None).Length - 1);

        var analysisHandlerStart = mainCode.IndexOf("private void AuthorizeAnonymousVisualAnalysis_Click", StringComparison.Ordinal);
        var analysisHandlerEnd = mainCode.IndexOf("private async void Start_Click", analysisHandlerStart, StringComparison.Ordinal);
        var analysisHandler = mainCode[analysisHandlerStart..analysisHandlerEnd];
        Assert.True(
            analysisHandler.IndexOf("dialog.ShowDialog() != true", StringComparison.Ordinal) <
            analysisHandler.IndexOf("AnonymousVisualAnalysisAuthorization.GrantForSession", StringComparison.Ordinal));

        var invalidationStart = mainCode.IndexOf("private void InvalidateVisualAuthorization()", StringComparison.Ordinal);
        var invalidationEnd = mainCode.IndexOf("private void SetVisualCaptureState", invalidationStart, StringComparison.Ordinal);
        var invalidation = mainCode[invalidationStart..invalidationEnd];
        Assert.Contains("_visualCaptureAuthorization = null", invalidation, StringComparison.Ordinal);
        Assert.Contains("_anonymousVisualAnalysisAuthorization = null", invalidation, StringComparison.Ordinal);

        var teardownStart = mainCode.IndexOf("private async Task StopAndDisposeVisualCaptureAsync", StringComparison.Ordinal);
        var teardownEnd = mainCode.IndexOf("private void OnVisualCaptureStateChanged", teardownStart, StringComparison.Ordinal);
        var teardown = mainCode[teardownStart..teardownEnd];
        Assert.True(
            teardown.IndexOf("await controller.DisposeAsync()", StringComparison.Ordinal) <
            teardown.IndexOf("await analysisSession.CompleteAt(finalOffset)", StringComparison.Ordinal));
        Assert.True(
            teardown.IndexOf("await analysisSession.CompleteAt(finalOffset)", StringComparison.Ordinal) <
            teardown.IndexOf("await analysisSession.DisposeAsync()", StringComparison.Ordinal));
        Assert.True(
            teardown.IndexOf("await analysisSession.DisposeAsync()", StringComparison.Ordinal) <
            teardown.IndexOf("await RefreshActiveVisualEvidenceAsync", StringComparison.Ordinal));
        Assert.Contains("allowAnalyzing: false", teardown, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void TranscriptTemplates_ShowNonInteractiveAnonymousEvidenceWithLiveAnnouncementsOnlyInLiveView()
    {
        var root = FindRepositoryRoot();
        var mainXaml = File.ReadAllText(Path.Combine(
            root, "src", "Trazio.AsistenteReunion.App", "MainWindow.xaml"));
        var liveStart = mainXaml.IndexOf("x:Name=\"LiveTranscript\"", StringComparison.Ordinal);
        var liveEnd = mainXaml.IndexOf("x:Name=\"HistoryTabItem\"", liveStart, StringComparison.Ordinal);
        var historyStart = mainXaml.IndexOf("x:Name=\"HistorySegments\"", StringComparison.Ordinal);
        var historyEnd = mainXaml.IndexOf("x:Name=\"CorrectionPanel\"", historyStart, StringComparison.Ordinal);
        var comparisonStart = mainXaml.IndexOf("x:Name=\"ComparisonRows\"", StringComparison.Ordinal);
        var comparisonEnd = mainXaml.IndexOf("</ListBox>", comparisonStart, StringComparison.Ordinal) + "</ListBox>".Length;
        var liveTemplate = mainXaml[liveStart..liveEnd];
        var historyTemplate = mainXaml[historyStart..historyEnd];
        var comparisonTemplate = mainXaml[comparisonStart..comparisonEnd];

        Assert.Equal(2, mainXaml.Split("Text=\"{Binding VisualEvidence.DisplayText}\"", StringSplitOptions.None).Length - 1);
        Assert.Equal(2, mainXaml.Split("Visibility=\"{Binding VisualEvidence.Visibility}\"", StringSplitOptions.None).Length - 1);
        Assert.Equal(2, mainXaml.Split("automation:AutomationProperties.Name=\"{Binding VisualEvidence.AutomationName}\"", StringSplitOptions.None).Length - 1);
        Assert.Equal(2, mainXaml.Split("automation:AutomationProperties.HelpText=\"{Binding VisualEvidence.HelpText}\"", StringSplitOptions.None).Length - 1);
        Assert.Contains("Focusable=\"False\" IsHitTestVisible=\"False\"", liveTemplate, StringComparison.Ordinal);
        Assert.Contains("Focusable=\"False\" IsHitTestVisible=\"False\"", historyTemplate, StringComparison.Ordinal);
        Assert.Contains("automation:AutomationProperties.LiveSetting=\"Polite\"", liveTemplate, StringComparison.Ordinal);
        Assert.DoesNotContain("automation:AutomationProperties.LiveSetting", historyTemplate, StringComparison.Ordinal);
        Assert.DoesNotContain("VisualEvidence", comparisonTemplate, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Trazio.AsistenteReunion.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found from the test output directory.");
    }

    [Fact]
    public void AudioRetention_IsRequiredForNewAndLegacySettings()
    {
        var legacySettings = new AppSettings(KeepEncryptedAudio: false);
        var compiledApplication = Encoding.UTF8.GetString(File.ReadAllBytes(typeof(MainWindow).Assembly.Location));

        Assert.True(AudioRetentionPolicy.Enforce(new AppSettings()).KeepEncryptedAudio);
        Assert.True(AudioRetentionPolicy.Enforce(legacySettings).KeepEncryptedAudio);
        Assert.Contains("El audio cifrado siempre se guarda para reproducirlo y retranscribirlo", compiledApplication);
        Assert.Contains("Solo una exportación WAV explícita crea un archivo reproducible por otras aplicaciones", compiledApplication);
    }
}

