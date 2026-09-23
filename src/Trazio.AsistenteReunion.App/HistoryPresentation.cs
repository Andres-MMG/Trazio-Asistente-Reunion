using System.IO;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.App;

public sealed record HistorySessionItem(SessionSummary Session, string Title, string Details)
{
    public static HistorySessionItem From(SessionSummary session, TimeZoneInfo? timeZone = null)
    {
        timeZone ??= TimeZoneInfo.Local;
        var localStart = TimeZoneInfo.ConvertTime(session.StartedAt, timeZone);
        return new(session, session.Title, $"{localStart:yyyy-MM-dd HH:mm} · {StateName(session.State)} · {MeetingProviderPresentation.Name(session.MeetingProvider)}");
    }

    private static string StateName(SessionState state) => state switch
    {
        SessionState.Recording => "Grabando",
        SessionState.Paused => "Pausada",
        SessionState.Completed => "Completada",
        SessionState.Interrupted => "Interrumpida",
        _ => state.ToString()
    };
}

public sealed record HistoryViewState(
    bool HasSelection,
    bool HasTranscript,
    bool HasAnyAudio,
    bool HasSelectedSourceAudio,
    SessionState? SessionState,
    bool IsActiveSession,
    AudioSourceKind SelectedSource,
    string TranscriptContent,
    string AudioGuidance,
    string ActionGuidance)
{
    public bool CanPlayAudio => HasSelection && HasSelectedSourceAudio;
    public bool CanExportWav => CanPlayAudio;
    public bool CanExportTxt => HasSelection && HasTranscript;
    public bool CanExportMarkdown => CanExportTxt;
    public bool CanCorrectTranscript => HasSelection && HasTranscript;
    public bool CanRetranscribe => HasSelection && HasTranscript && HasSelectedSourceAudio;
    public bool CanDelete => HasSelection && !IsActiveSession;
    public bool CanChooseSource => HasSelection && HasAnyAudio;
    public string DeleteReason => IsActiveSession
        ? "Esta sesión sigue activa. Detenla antes de eliminarla."
        : HasSelection ? "Elimina definitivamente la sesión seleccionada, su transcripción y el audio conservado." : SelectSessionReason;

    private const string SelectSessionReason = "Selecciona primero una sesión guardada.";
}

public static class HistoryPresenter
{
    public const string SelectSessionMessage = "Selecciona una sesión guardada para ver su transcripción y el audio conservado.";
    public const string NoTranscriptMessage = "Esta sesión no tiene una transcripción guardada. El audio podría seguir disponible para reproducirlo o exportarlo.";

    public static HistoryViewState Create(
        bool hasSelection,
        SessionState? sessionState,
        IReadOnlyList<TranscriptSegment> segments,
        IReadOnlyList<AudioArchiveSummary> audio,
        AudioSourceKind requestedSource,
        Func<IEnumerable<TranscriptSegment>, string> formatTranscript)
    {
        if (!hasSelection)
            return new(false, false, false, false, null, false, requestedSource, SelectSessionMessage, SelectSessionMessage, "Selecciona una sesión para habilitar las acciones disponibles.");

        var microphone = audio.Any(item => item.Source == AudioSourceKind.Microphone && item.ChunkCount > 0);
        var output = audio.Any(item => item.Source == AudioSourceKind.SystemOutput && item.ChunkCount > 0);
        var selected = requestedSource;
        if (selected == AudioSourceKind.Microphone && !microphone && output) selected = AudioSourceKind.SystemOutput;
        else if (selected == AudioSourceKind.SystemOutput && !output && microphone) selected = AudioSourceKind.Microphone;

        var hasAnyAudio = microphone || output;
        var hasSelectedAudio = selected == AudioSourceKind.Microphone ? microphone : output;
        var isActiveSession = sessionState is SessionState.Recording or SessionState.Paused;
        var sourceName = selected == AudioSourceKind.Microphone ? "micrófono" : "audio del equipo";
        var audioGuidance = hasSelectedAudio
            ? $"Hay audio conservado del {sourceName}."
            : hasAnyAudio
                ? $"No hay audio conservado del {sourceName}. Elige la otra fuente."
                : "No hay audio conservado. No se puede reproducir ni retranscribir porque esta sesión no guardó audio o porque el audio más antiguo se eliminó al alcanzar el límite de almacenamiento.";

        return new(
            true,
            segments.Count > 0,
            hasAnyAudio,
            hasSelectedAudio,
            sessionState,
            isActiveSession,
            selected,
            segments.Count > 0 ? formatTranscript(segments) : NoTranscriptMessage,
            audioGuidance,
            isActiveSession ? "Esta sesión sigue activa. Detenla antes de eliminarla." : "Las acciones disponibles corresponden a la transcripción y al audio conservado que se muestran arriba.");
    }
}

public sealed record StorageLocationFacts(string DataDirectory, string DatabasePath, string AudioDirectory)
{
    public static StorageLocationFacts Current() => new(
        ApplicationPaths.DataDirectory,
        ApplicationPaths.DatabasePath,
        ApplicationPaths.AudioDirectory);

    public string Explanation =>
        "Las transcripciones están cifradas dentro de trazio-transcripts.db. El audio conservado está cifrado en la carpeta audio y no se puede abrir directamente. Las exportaciones TXT, Markdown y WAV no están cifradas y se guardan solo en la ubicación que elijas.";
}

public sealed record StorageLocationUiState(
    string Status,
    bool CanChange,
    bool CanRestoreDefault,
    bool CanCancelPending);

public static class StorageLocationPresenter
{
    public static StorageLocationUiState Create(
        string currentRoot,
        string defaultRoot,
        StorageLocationState locatorState,
        bool initialized,
        bool recording,
        bool playing,
        bool busy,
        bool closing)
    {
        var available = initialized && !recording && !playing && !busy && !closing;
        var pending = locatorState.Pending;
        var committedCleanup = pending is not null &&
                               locatorState.CleanupPendingSource is not null &&
                               string.Equals(Path.GetFullPath(currentRoot), Path.GetFullPath(pending.TargetRoot), StringComparison.OrdinalIgnoreCase);
        var status = pending is null
            ? "La carpeta actual está activa. Los cambios de almacenamiento se aplican solo después de un traslado verificado al reiniciar."
            : committedCleanup
                ? $"La carpeta actual está activa: {currentRoot}{Environment.NewLine}La limpieza verificada de la carpeta anterior está pendiente y continuará en el próximo inicio. Este traslado confirmado ya no se puede cancelar."
                : $"Traslado pendiente a: {pending.TargetRoot}{Environment.NewLine}Debes reiniciar. Hasta entonces, Abrir carpeta y Copiar ruta seguirán usando la carpeta actual. Puedes cancelar antes de reiniciar.";
        var isDefault = string.Equals(Path.GetFullPath(currentRoot), Path.GetFullPath(defaultRoot), StringComparison.OrdinalIgnoreCase);
        return new(
            status,
            available && pending is null,
            available && pending is null && !isDefault,
            available && pending is not null && !committedCleanup);
    }
}



public sealed record HistoryRetranscriptionUiState(bool CanStart, bool CanCancel, string Guidance);

public static class HistoryRetranscriptionPresenter
{
    public static HistoryRetranscriptionUiState Create(SessionState? sessionState, bool hasSelectedSourceAudio, bool isRunning)
    {
        if (isRunning) return new(false, true, "La retranscripción está en curso. Puedes cancelarla sin modificar la transcripción original.");
        if (sessionState is SessionState.Recording or SessionState.Paused)
            return new(false, false, "Detén la sesión activa antes de retranscribirla.");
        if (sessionState is not (SessionState.Completed or SessionState.Interrupted))
            return new(false, false, "Selecciona primero una sesión guardada que esté completada o interrumpida.");
        if (!hasSelectedSourceAudio)
            return new(false, false, "No se puede retranscribir porque esta fuente no conserva el audio completo; el audio eliminado no se puede recuperar.");
        return new(true, false, "Hay audio conservado disponible para crear una versión separada del modelo.");
    }
}
