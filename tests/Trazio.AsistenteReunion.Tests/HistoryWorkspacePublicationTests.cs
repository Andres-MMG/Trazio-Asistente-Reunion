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

