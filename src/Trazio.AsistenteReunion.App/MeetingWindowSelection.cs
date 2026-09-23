using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.App;

public sealed record MeetingWindowCandidate(
    nint Handle,
    uint ProcessId,
    string Title,
    string? ProcessName,
    MeetingProvider Provider,
    bool IsMinimized)
{
    public string ApplicationName => string.IsNullOrWhiteSpace(ProcessName) ? "No disponible" : ProcessName;
    public string ProviderName => MeetingProviderPresentation.Name(Provider);
    public string MinimizedName => IsMinimized ? "Sí" : "No";
}

public sealed record MeetingWindowSelection(
    nint Handle,
    uint ProcessId,
    MeetingProvider Provider);

public interface IMeetingWindowCatalog
{
    IReadOnlyList<MeetingWindowCandidate> Enumerate();
    bool IsAvailable(MeetingWindowSelection selection);
}

public static class MeetingWindowClassifier
{
    public static MeetingProvider Classify(string? title, string? processName)
    {
        var meet = Contains(title, "Google Meet") || Contains(title, "meet.google.com");
        var teams = Contains(title, "Microsoft Teams") ||
                    Contains(title, "teams.microsoft.com") ||
                    IsTeamsProcess(processName);

        if (meet == teams) return MeetingProvider.Other;
        return meet ? MeetingProvider.GoogleMeet : MeetingProvider.MicrosoftTeams;
    }

    private static bool Contains(string? source, string value) =>
        source?.Contains(value, StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsTeamsProcess(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return false;
        var normalized = processName.Trim();
        if (normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[..^4];
        return normalized.Equals("ms-teams", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("teams", StringComparison.OrdinalIgnoreCase);
    }
}

public static class MeetingProviderPresentation
{
    public static string Name(MeetingProvider provider) => provider switch
    {
        MeetingProvider.NotSelected => "Sin seleccionar",
        MeetingProvider.GoogleMeet => "Google Meet",
        MeetingProvider.MicrosoftTeams => "Microsoft Teams",
        MeetingProvider.Other => "Otra aplicación",
        _ => "Otra aplicación"
    };
}

public enum MeetingWindowAvailability
{
    NotSelected,
    Available,
    Lost,
    AlreadyLost
}

public sealed class MeetingWindowSelectionController(IMeetingWindowCatalog catalog)
{
    public MeetingWindowSelection? Selection { get; private set; }
    public bool IsLost { get; private set; }

    public void Select(MeetingWindowSelection selection)
    {
        Selection = selection;
        IsLost = false;
    }

    public void Clear()
    {
        Selection = null;
        IsLost = false;
    }

    public MeetingProvider ResolveProviderForStart()
    {
        if (Selection is null) return MeetingProvider.NotSelected;
        if (!IsLost && IsSelectionAvailable()) return Selection.Provider;

        Selection = null;
        IsLost = true;
        return MeetingProvider.NotSelected;
    }

    public MeetingWindowAvailability CheckWhileRecording()
    {
        if (Selection is null) return MeetingWindowAvailability.NotSelected;
        if (IsLost) return MeetingWindowAvailability.AlreadyLost;
        if (IsSelectionAvailable()) return MeetingWindowAvailability.Available;

        IsLost = true;
        return MeetingWindowAvailability.Lost;
    }

    public void FinishSession() => Clear();

    private bool IsSelectionAvailable()
    {
        try
        {
            return Selection is not null && catalog.IsAvailable(Selection);
        }
        catch (Exception)
        {
            return false;
        }
    }
}

public sealed record MeetingWindowSelectionUiState(
    string Status,
    string SelectButtonText,
    bool CanSelect,
    bool CanClear);

public static class MeetingWindowSelectionPresenter
{
    public static MeetingWindowSelectionUiState Create(
        MeetingWindowSelection? selection,
        bool isLost,
        bool isRecording,
        bool isReady)
    {
        if (isLost)
            return new(
                isRecording
                    ? "La ventana asociada ya no está disponible. La transcripción continúa sin cambiar la captura de audio."
                    : "La ventana asociada ya no está disponible. Puedes elegir otra o continuar sin seleccionar.",
                "Seleccionar…",
                !isRecording && isReady,
                false);

        if (selection is null)
            return new(
                "Sin seleccionar. La captura seguirá usando los dispositivos de audio configurados.",
                "Seleccionar…",
                !isRecording && isReady,
                false);

        var provider = MeetingProviderPresentation.Name(selection.Provider);
        return new(
            isRecording
                ? $"{provider} asociado a esta sesión. Solo se conserva el proveedor; el análisis visual requiere una autorización separada y no guarda el título."
                : $"{provider} seleccionado para la próxima sesión. Asociarlo no inicia el análisis visual ni guarda el título.",
            "Cambiar…",
            !isRecording && isReady,
            !isRecording && isReady);
    }
}

public sealed class Win32MeetingWindowCatalog : IMeetingWindowCatalog
{
    private const int ExtendedStyleIndex = -20;
    private const long ToolWindowStyle = 0x00000080L;
    private const int CloakedAttribute = 14;
    private readonly uint _ownProcessId = (uint)Environment.ProcessId;

    public IReadOnlyList<MeetingWindowCandidate> Enumerate()
    {
        var candidates = new List<MeetingWindowCandidate>();
        NativeMethods.EnumWindows((handle, _) =>
        {
            TryAddCandidate(handle, candidates);
            return true;
        }, nint.Zero);
        return candidates
            .OrderBy(candidate => candidate.Provider == MeetingProvider.Other ? 1 : 0)
            .ThenBy(candidate => candidate.ApplicationName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(candidate => candidate.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public bool IsAvailable(MeetingWindowSelection selection)
    {
        try
        {
            if (!NativeMethods.IsWindow(selection.Handle)) return false;
            NativeMethods.GetWindowThreadProcessId(selection.Handle, out var processId);
            return processId != 0 && processId == selection.ProcessId;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void TryAddCandidate(nint handle, ICollection<MeetingWindowCandidate> candidates)
    {
        if (!NativeMethods.IsWindowVisible(handle) || IsToolWindow(handle) || IsCloaked(handle)) return;
        NativeMethods.GetWindowThreadProcessId(handle, out var processId);
        if (processId == 0 || processId == _ownProcessId) return;

        var title = ReadTitle(handle);
        if (string.IsNullOrWhiteSpace(title)) return;
        var processName = TryReadProcessName(processId);
        candidates.Add(new(
            handle,
            processId,
            title.Trim(),
            processName,
            MeetingWindowClassifier.Classify(title, processName),
            NativeMethods.IsIconic(handle)));
    }

    private static bool IsToolWindow(nint handle) =>
        (NativeMethods.GetExtendedStyle(handle).ToInt64() & ToolWindowStyle) != 0;

    private static bool IsCloaked(nint handle) =>
        NativeMethods.DwmGetWindowAttribute(handle, CloakedAttribute, out var cloaked, sizeof(int)) == 0 && cloaked != 0;

    private static string ReadTitle(nint handle)
    {
        var length = NativeMethods.GetWindowTextLength(handle);
        if (length <= 0) return string.Empty;
        var buffer = new StringBuilder(length + 1);
        return NativeMethods.GetWindowText(handle, buffer, buffer.Capacity) > 0 ? buffer.ToString() : string.Empty;
    }

    private static string? TryReadProcessName(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById(checked((int)processId));
            return process.ProcessName;
        }
        catch (SystemException)
        {
            return null;
        }
    }

    private static class NativeMethods
    {
        internal delegate bool EnumWindowsCallback(nint handle, nint parameter);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindowVisible(nint handle);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindow(nint handle);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsIconic(nint handle);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetWindowText(nint handle, StringBuilder text, int maximumCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetWindowTextLength(nint handle);

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(nint handle, out uint processId);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern nint GetWindowLongPtr64(nint handle, int index);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern int GetWindowLong32(nint handle, int index);

        internal static nint GetExtendedStyle(nint handle) =>
            nint.Size == 8 ? GetWindowLongPtr64(handle, ExtendedStyleIndex) : (nint)GetWindowLong32(handle, ExtendedStyleIndex);

        [DllImport("dwmapi.dll")]
        internal static extern int DwmGetWindowAttribute(nint handle, int attribute, out int value, int size);
    }
}
