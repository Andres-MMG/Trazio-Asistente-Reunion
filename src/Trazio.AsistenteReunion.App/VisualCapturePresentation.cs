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
                "Análisis visual desactivado. Selecciona una ventana para poder autorizarlo.",
            VisualCaptureState.Off when hasPendingAuthorization =>
                isRecordingPaused
                    ? "Análisis visual autorizado, pero no se iniciará mientras la reunión esté pausada. Reanuda la reunión para continuar."
                    : "Análisis visual autorizado para la ventana seleccionada. Se iniciará después de confirmar el audio de la sesión.",
            VisualCaptureState.Off =>
                "Análisis visual desactivado. Seleccionar una ventana no lo activa.",
            VisualCaptureState.Starting =>
                "Iniciando el análisis visual de la ventana seleccionada…",
            VisualCaptureState.Active =>
                "Análisis visual activo · no se guardan imágenes",
            VisualCaptureState.Pausing =>
                "Pausando el análisis visual…",
            VisualCaptureState.Paused =>
                "Análisis visual pausado. El audio y la transcripción continúan.",
            VisualCaptureState.TargetMinimized =>
                "La ventana está minimizada. El análisis visual está pausado; el audio y la transcripción continúan.",
            VisualCaptureState.Stopping =>
                "Deteniendo el análisis visual…",
            VisualCaptureState.Stopped =>
                "Análisis visual detenido para esta sesión. El audio y la transcripción continúan.",
            VisualCaptureState.NotSupported =>
                "Este equipo no admite análisis visual de ventanas. La transcripción continúa sin cambios.",
            VisualCaptureState.TargetUnavailable =>
                "Se perdió la ventana seleccionada. El análisis visual se detuvo; el audio y la transcripción continúan.",
            VisualCaptureState.ProtectedContent =>
                "El contenido no permite análisis visual. Trazio no intentará omitir esta protección.",
            _ =>
                "No se pudo continuar el análisis visual. El audio y la transcripción siguen activos."
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
