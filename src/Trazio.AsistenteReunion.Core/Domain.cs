namespace Trazio.AsistenteReunion.Core;

public enum AudioSourceKind { Microphone, SystemOutput }
public enum SessionState { Recording, Paused, Completed, Interrupted }
public enum MeetingProvider
{
    NotSelected = 0,
    GoogleMeet = 1,
    MicrosoftTeams = 2,
    Other = 3
}

public sealed record AudioChunk(
    string Id, string SessionId, AudioSourceKind Source, long Sequence,
    DateTimeOffset CapturedAt, byte[] Pcm16, int SampleRate = 16_000)
{
    public static AudioChunk Create(string sessionId, AudioSourceKind source, long sequence, DateTimeOffset at, byte[] pcm) =>
        new($"{sessionId}:{source}:{sequence}", sessionId, source, sequence, at, pcm);
}

public sealed record TranscriptSegment(
    string Id, string SessionId, AudioSourceKind Source, long Sequence,
    TimeSpan Start, TimeSpan End, string Text, DateTimeOffset CreatedAt,
    string? SpeakerName = null);

public sealed record MeetingSession(
    string Id, string Title, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, SessionState State,
    string? LocalSpeakerName = null,
    MeetingProvider MeetingProvider = MeetingProvider.NotSelected);

public sealed record SessionSummary(
    string Id, string Title, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, SessionState State,
    string? LocalSpeakerName = null,
    MeetingProvider MeetingProvider = MeetingProvider.NotSelected);

public sealed record ArchivedAudioChunk(
    string Id, string SessionId, AudioSourceKind Source, long Sequence,
    DateTimeOffset StartedAt, TimeSpan Duration, string RelativePath, long EncryptedBytes);

public sealed record AudioArchiveSummary(AudioSourceKind Source, int ChunkCount, TimeSpan Duration, long EncryptedBytes);

public sealed record AppSettings(
    string? MicrophoneDeviceId = null,
    string? OutputDeviceId = null,
    string? ModelPath = null,
    string Language = "es",
    bool CaptureMicrophone = true,
    bool CaptureSystemOutput = true,
    bool KeepEncryptedAudio = false,
    int AudioStorageBudgetGb = 1,
    string? LocalDisplayName = null,
    string? LocalOrganization = null,
    bool LocalProfileConfirmed = false);

public static class AudioRetentionPolicy
{
    public const bool Required = true;

    public static AppSettings Enforce(AppSettings settings) =>
        settings.KeepEncryptedAudio ? settings : settings with { KeepEncryptedAudio = Required };
}

public static class LocalProfile
{
    public static AppSettings EnsureDefault(AppSettings settings, string windowsAccountName)
    {
        if (!string.IsNullOrWhiteSpace(settings.LocalDisplayName)) return settings;
        var defaultName = string.IsNullOrWhiteSpace(windowsAccountName) ? "Local user" : windowsAccountName.Trim();
        return settings with { LocalDisplayName = defaultName, LocalProfileConfirmed = false };
    }

    public static AppSettings SaveConfirmedProfile(
        AppSettings settings, string? displayName, string? organization, bool confirmed)
    {
        var normalizedName = displayName?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedName))
            throw new InvalidOperationException("Ingresa un nombre visible antes de guardar el perfil local.");
        if (!confirmed)
            throw new InvalidOperationException("Confirma el nombre visible antes de guardar el perfil local.");

        return settings with
        {
            LocalDisplayName = normalizedName,
            LocalOrganization = string.IsNullOrWhiteSpace(organization) ? null : organization.Trim(),
            LocalProfileConfirmed = true
        };
    }
    public static string ResolveMeetingDisplayName(AppSettings settings, string? meetingOverride)
    {
        var overrideName = meetingOverride?.Trim();
        if (!string.IsNullOrWhiteSpace(overrideName)) return overrideName;

        var profileName = settings.LocalDisplayName?.Trim();
        if (!settings.LocalProfileConfirmed || string.IsNullOrWhiteSpace(profileName))
            throw new InvalidOperationException("Confirma tu nombre visible o ingresa un nombre para esta reunión antes de iniciar la transcripción.");

        return profileName;
    }
}

public static class ApplicationPaths
{
    public const string ProductFolderName = "Trazio Asistente Reunion";
    private static readonly object Sync = new();
    private static string _dataDirectory = DefaultDataDirectory;
    private static bool _configured;

    public static string DefaultDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductFolderName);
    public static string DataDirectory => Volatile.Read(ref _dataDirectory);
    public static string ModelsDirectory => Path.Combine(DataDirectory, "models");
    public static string DatabasePath => Path.Combine(DataDirectory, "trazio-transcripts.db");
    public static string KeyPath => Path.Combine(DataDirectory, "master.key");
    public static string SettingsPath => Path.Combine(DataDirectory, "settings.dat");
    public static string AudioDirectory => Path.Combine(DataDirectory, "audio");

    public static void ConfigureOnce(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var fullPath = Path.GetFullPath(dataDirectory);
        lock (Sync)
        {
            if (_configured) throw new InvalidOperationException("Las rutas de la aplicación ya fueron configuradas.");
            _dataDirectory = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            _configured = true;
        }
    }
}