namespace Trazio.AsistenteReunion.Core;

/// <summary>
/// Keeps a process-wide handle to the mutex observed by the offline installer.
/// This marker is intentionally separate from the named-pipe single-instance guard:
/// the application and its transcription worker may hold it at the same time.
/// </summary>
public sealed class ApplicationRunningMarker : IDisposable
{
    public const string MutexName = "Trazio.AsistenteReunion.AppRunning.v1";

    private Mutex? _mutex;

    private ApplicationRunningMarker(Mutex mutex) => _mutex = mutex;

    public static ApplicationRunningMarker Create() => CreateNamed(MutexName);

    internal static ApplicationRunningMarker CreateNamed(string mutexName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mutexName);
        return new(new Mutex(initiallyOwned: false, mutexName));
    }

    public void Dispose() => Interlocked.Exchange(ref _mutex, null)?.Dispose();
}
