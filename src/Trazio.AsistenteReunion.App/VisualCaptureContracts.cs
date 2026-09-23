namespace Trazio.AsistenteReunion.App;

public enum VisualCaptureState
{
    Off,
    Starting,
    Active,
    Pausing,
    Paused,
    Stopping,
    Stopped,
    NotSupported,
    TargetUnavailable,
    ProtectedContent,
    Failed
}

public enum VisualCaptureExitReason
{
    Completed,
    Cancelled,
    Minimized,
    TargetLost,
    ProtectedContent,
    NotSupported,
    DeviceLost,
    UnexpectedFailure
}

public readonly record struct VisualCaptureExit(VisualCaptureExitReason Reason);

public interface IVisualCaptureLease : IAsyncDisposable
{
    Task<VisualCaptureExit> Completion { get; }
}

public interface IVisualFrameLease : IDisposable;

public interface IFrameSampler<T> : IAsyncDisposable where T : IDisposable
{
    long DroppedCount { get; }
    bool TryWrite(T item);
    Task RunAsync(Func<T, CancellationToken, ValueTask> process, CancellationToken cancellationToken = default);
    void Complete();
}

public interface IVisualMeetingCapture
{
    ValueTask<IVisualCaptureLease> StartValidatedAsync(CancellationToken cancellationToken);
}

public sealed class VisualCaptureConsent
{
    private readonly Guid _sessionId;
    private int _consumed;

    private VisualCaptureConsent(Guid sessionId) => _sessionId = sessionId;

    public static VisualCaptureConsent GrantForSession(Guid sessionId)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("A session identifier is required.", nameof(sessionId));
        return new VisualCaptureConsent(sessionId);
    }

    internal bool TryConsume(Guid sessionId) =>
        sessionId == _sessionId && Interlocked.Exchange(ref _consumed, 1) == 0;
}
