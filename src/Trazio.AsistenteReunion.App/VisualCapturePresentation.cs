using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.App;

internal sealed record VisualCaptureUiState(
    string Status,
    bool CanAuthorize,
    bool CanPause,
    bool CanResume,
    bool CanStop,
    bool ShowStop,
    bool ShowActiveIndicator);

internal static class VisualCapturePresenter
{
    public static VisualCaptureUiState Create(
        VisualCaptureState state,
        bool hasValidSelection,
        bool hasPendingAuthorization,
        bool isReady,
        bool isRecordingPaused,
        bool actionInProgress)
    {
        var status = state switch
        {
            VisualCaptureState.Off when !hasValidSelection =>
                "Captura visual desactivada. Selecciona una ventana para poder autorizarla.",
            VisualCaptureState.Off when hasPendingAuthorization =>
                isRecordingPaused
                    ? "Captura visual autorizada, pero no se iniciará mientras la reunión esté pausada. Reanuda la reunión para continuar."
                    : "Captura visual autorizada para la ventana seleccionada. Se iniciará después de confirmar el audio de la sesión.",
            VisualCaptureState.Off =>
                "Captura visual desactivada. Seleccionar una ventana no la activa.",
            VisualCaptureState.Starting =>
                "Iniciando la captura visual de la ventana seleccionada…",
            VisualCaptureState.Active =>
                "Captura visual activa · no se guardan imágenes",
            VisualCaptureState.Pausing =>
                "Pausando la captura visual…",
            VisualCaptureState.Paused =>
                "Captura visual pausada. El audio y la transcripción continúan.",
            VisualCaptureState.TargetMinimized =>
                "La ventana está minimizada. La captura visual está pausada; el audio y la transcripción continúan.",
            VisualCaptureState.Stopping =>
                "Deteniendo la captura visual…",
            VisualCaptureState.Stopped =>
                "Captura visual detenida para esta sesión. El audio y la transcripción continúan.",
            VisualCaptureState.NotSupported =>
                "Este equipo no admite captura visual de ventanas. La transcripción continúa sin cambios.",
            VisualCaptureState.TargetUnavailable =>
                "Se perdió la ventana seleccionada. La captura visual se detuvo; el audio y la transcripción continúan.",
            VisualCaptureState.ProtectedContent =>
                "El contenido no permite captura visual. Trazio no intentará omitir esta protección.",
            _ =>
                "No se pudo continuar la captura visual. El audio y la transcripción siguen activos."
        };

        var available = isReady && !actionInProgress;
        return new(
            status,
            available && !isRecordingPaused && state == VisualCaptureState.Off && hasValidSelection && !hasPendingAuthorization,
            available && state == VisualCaptureState.Active,
            available && state is VisualCaptureState.Paused or VisualCaptureState.TargetMinimized,
            available && state is VisualCaptureState.Starting or VisualCaptureState.Active or
                VisualCaptureState.Pausing or VisualCaptureState.Paused or VisualCaptureState.TargetMinimized,
            state is VisualCaptureState.Starting or VisualCaptureState.Active or VisualCaptureState.Pausing or
                VisualCaptureState.Paused or VisualCaptureState.TargetMinimized or VisualCaptureState.Stopping,
            state == VisualCaptureState.Active);
    }
}

internal sealed class VisualCaptureStateTracker
{
    private Guid _sourceId;
    private long _revision;

    public void Reset(Guid sourceId)
    {
        _sourceId = sourceId;
        _revision = 0;
    }

    public bool TryAccept(VisualCaptureStateChange change)
    {
        if (change.SourceId != _sourceId || change.Revision <= _revision) return false;
        _revision = change.Revision;
        return true;
    }
}

internal static class VisualCaptureActivationPolicy
{
    public static bool CanStart(bool isRecording, bool isRecordingPaused, bool isClosing) =>
        isRecording && !isRecordingPaused && !isClosing;
}

internal enum AnonymousVisualAnalysisStatus
{
    Off = 0,
    Authorized = 1,
    Running = 2,
    ProfileUnavailable = 3,
    Paused = 4,
    Stopped = 5,
    Failed = 6
}

internal sealed record AnonymousVisualAnalysisUiState(
    string Status,
    bool CanAuthorize);

internal static class AnonymousVisualAnalysisPresenter
{
    public static AnonymousVisualAnalysisUiState Create(
        AnonymousVisualAnalysisStatus status,
        bool hasValidSelection,
        MeetingProvider provider,
        bool isRecording,
        bool captureSystemOutput,
        VisualCaptureState visualCaptureState,
        bool isReady,
        bool isRecordingPaused,
        bool actionInProgress)
    {
        var supportedProvider = provider is MeetingProvider.GoogleMeet or MeetingProvider.MicrosoftTeams;
        var message = status switch
        {
            AnonymousVisualAnalysisStatus.Authorized =>
                "Actividad visual anónima autorizada para esta sesión. Autoriza la captura visual para comenzar.",
            AnonymousVisualAnalysisStatus.Running =>
                "Actividad visual anónima activa. Solo se guardan intervalos cifrados derivados.",
            AnonymousVisualAnalysisStatus.ProfileUnavailable =>
                "Evidencia visual no disponible: perfil en validación.",
            AnonymousVisualAnalysisStatus.Paused =>
                "Actividad visual anónima no disponible mientras la captura visual está pausada.",
            AnonymousVisualAnalysisStatus.Stopped =>
                "Actividad visual anónima no disponible hasta cerrar esta sesión.",
            AnonymousVisualAnalysisStatus.Failed =>
                "Evidencia visual no disponible por un problema. El audio y la transcripción continúan.",
            _ when !isRecording =>
                "Actividad visual anónima desactivada. Inicia una sesión con audio del equipo para autorizarla.",
            _ when !captureSystemOutput =>
                "Evidencia visual no disponible: esta sesión no captura audio del equipo.",
            _ when !hasValidSelection =>
                "Evidencia visual no disponible: no hay una ventana de reunión seleccionada.",
            _ when !supportedProvider =>
                "Evidencia visual no disponible: solo se admite Google Meet o Microsoft Teams.",
            _ when visualCaptureState != VisualCaptureState.Off =>
                "Evidencia visual no disponible: la captura visual ya comenzó sin autorización de análisis anónimo.",
            _ =>
                "Actividad visual anónima desactivada. Puedes autorizarla antes de iniciar la captura visual."
        };

        var canAuthorize =
            status == AnonymousVisualAnalysisStatus.Off &&
            hasValidSelection &&
            supportedProvider &&
            isRecording &&
            captureSystemOutput &&
            visualCaptureState == VisualCaptureState.Off &&
            isReady &&
            !isRecordingPaused &&
            !actionInProgress;

        return new(message, canAuthorize);
    }
}
