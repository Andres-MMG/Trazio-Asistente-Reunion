namespace Trazio.AsistenteReunion.App;

public enum VisualCaptureState
{
    Off,
    Starting,
    Active,
    Pausing,
    Paused,
    TargetMinimized,
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

public readonly record struct VisualCaptureStateChange(
    Guid SourceId,
    long Revision,
    VisualCaptureState State);

public interface IVisualCaptureStateObserver
{
    void OnStateChanged(VisualCaptureStateChange change);
}

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

public sealed class VisualCaptureAuthorization
{
    private readonly MeetingWindowSelection _selection;
    private int _consumed;

    private VisualCaptureAuthorization(MeetingWindowSelection selection)
    {
        _selection = selection;
        AuthorizationId = Guid.NewGuid();
    }

    public Guid AuthorizationId { get; }

    public static VisualCaptureAuthorization GrantForSelection(MeetingWindowSelection selection) =>
        new(selection ?? throw new ArgumentNullException(nameof(selection)));

    public bool Matches(MeetingWindowSelection? selection) =>
        selection is not null && selection == _selection && Volatile.Read(ref _consumed) == 0;

    internal bool TryConsume(
        MeetingWindowSelection selection,
        Guid sessionId,
        out VisualCaptureConsent? consent)
    {
        consent = null;
        if (sessionId == Guid.Empty || selection != _selection) return false;
        if (Interlocked.Exchange(ref _consumed, 1) != 0) return false;
        consent = VisualCaptureConsent.GrantForSession(sessionId);
        return true;
    }
}

internal interface IVisualCaptureStateSink
{
    void ReceiveVisualCaptureState(VisualCaptureStateChange change);
}

internal sealed class WeakVisualCaptureStateObserver<TTarget> : IVisualCaptureStateObserver
    where TTarget : class, IVisualCaptureStateSink
{
    private readonly WeakReference<TTarget> _target;

    public WeakVisualCaptureStateObserver(TTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        _target = new(target);
    }

    public void OnStateChanged(VisualCaptureStateChange change)
    {
        if (_target.TryGetTarget(out var target)) target.ReceiveVisualCaptureState(change);
    }
}
