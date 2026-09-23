namespace Trazio.AsistenteReunion.App;

public sealed class VisualCaptureSessionController : IAsyncDisposable
{
    private readonly Guid _sessionId;
    private readonly IVisualMeetingCapture _capture;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private IVisualCaptureLease? _activeCapture;
    private CancellationTokenSource? _captureCancellation;
    private Task _startCompletion = Task.CompletedTask;
    private Task _teardownCompletion = Task.CompletedTask;
    private bool _authorized;
    private bool _disposed;
    private long _generation;
    private int _state = (int)VisualCaptureState.Off;

    public VisualCaptureSessionController(Guid sessionId, IVisualMeetingCapture capture)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("A session identifier is required.", nameof(sessionId));
        _sessionId = sessionId;
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
    }

    public VisualCaptureState State => (VisualCaptureState)Volatile.Read(ref _state);

    public async ValueTask<VisualCaptureState> StartAsync(
        VisualCaptureConsent consent,
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task? pendingTransition = null;
            long generation = 0;
            CancellationTokenSource? captureCancellation = null;
            TaskCompletionSource? startCompletion = null;
            await _lifecycle.WaitAsync(cancellationToken);
            try
            {
                if (_disposed || State == VisualCaptureState.Stopped) return State;
                if (State is VisualCaptureState.Pausing or VisualCaptureState.Stopping ||
                    !_teardownCompletion.IsCompleted)
                {
                    pendingTransition = _teardownCompletion;
                }
                else if (State is VisualCaptureState.Starting or VisualCaptureState.Active)
                {
                    return State;
                }
                else
                {
                    if (!_authorized)
                    {
                        if (consent is null || !consent.TryConsume(_sessionId)) return State;
                        _authorized = true;
                    }

                    generation = ++_generation;
                    captureCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    _captureCancellation = captureCancellation;
                    startCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    _startCompletion = startCompletion.Task;
                    SetState(VisualCaptureState.Starting);
                }
            }
            finally
            {
                _lifecycle.Release();
            }

            if (pendingTransition is not null)
            {
                await pendingTransition.WaitAsync(cancellationToken);
                continue;
            }

            return await StartPreparedCaptureAsync(generation, captureCancellation!, startCompletion!);
        }
    }

    public async ValueTask<VisualCaptureState> PauseAsync()
    {
        TeardownTransition? transition = null;
        Task? pendingTransition = null;
        long generation = 0;
        await _lifecycle.WaitAsync();
        try
        {
            if (_disposed || State == VisualCaptureState.Stopped) return State;
            if (State == VisualCaptureState.Pausing)
            {
                pendingTransition = _teardownCompletion;
            }
            else if (State is VisualCaptureState.Starting or VisualCaptureState.Active)
            {
                generation = ++_generation;
                transition = BeginTeardown(TakeCancellation(), TakeCapture(), _startCompletion);
                SetState(VisualCaptureState.Pausing);
            }
            else
            {
                return State;
            }
        }
        finally
        {
            _lifecycle.Release();
        }

        if (pendingTransition is not null)
        {
            await pendingTransition;
            return State;
        }

        await ExecuteTeardownAsync(transition!, generation, VisualCaptureState.Pausing, VisualCaptureState.Paused);
        return State;
    }

    public async ValueTask<VisualCaptureState> ResumeAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task? pendingTransition = null;
            long generation = 0;
            CancellationTokenSource? captureCancellation = null;
            TaskCompletionSource? startCompletion = null;
            await _lifecycle.WaitAsync(cancellationToken);
            try
            {
                if (_disposed || State == VisualCaptureState.Stopped) return State;
                if (State == VisualCaptureState.Pausing || !_teardownCompletion.IsCompleted)
                {
                    pendingTransition = _teardownCompletion;
                }
                else if (State != VisualCaptureState.Paused || !_authorized)
                {
                    return State;
                }
                else
                {
                    generation = ++_generation;
                    captureCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    _captureCancellation = captureCancellation;
                    startCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    _startCompletion = startCompletion.Task;
                    SetState(VisualCaptureState.Starting);
                }
            }
            finally
            {
                _lifecycle.Release();
            }

            if (pendingTransition is not null)
            {
                await pendingTransition.WaitAsync(cancellationToken);
                continue;
            }

            return await StartPreparedCaptureAsync(generation, captureCancellation!, startCompletion!);
        }
    }

    public async ValueTask<VisualCaptureState> StopAsync()
    {
        while (true)
        {
            Task? pendingTransition = null;
            TeardownTransition? transition = null;
            long generation = 0;
            await _lifecycle.WaitAsync();
            try
            {
                if (_disposed || State == VisualCaptureState.Stopped) return State;
                if (State is VisualCaptureState.Pausing or VisualCaptureState.Stopping || !_teardownCompletion.IsCompleted)
                {
                    pendingTransition = _teardownCompletion;
                }
                else
                {
                    generation = ++_generation;
                    transition = BeginTeardown(TakeCancellation(), TakeCapture(), _startCompletion);
                    _authorized = false;
                    SetState(VisualCaptureState.Stopping);
                }
            }
            finally
            {
                _lifecycle.Release();
            }

            if (pendingTransition is not null)
            {
                await pendingTransition;
                continue;
            }

            await ExecuteTeardownAsync(transition!, generation, VisualCaptureState.Stopping, VisualCaptureState.Stopped);
            return State;
        }
    }

    public async ValueTask DisposeAsync()
    {
        while (true)
        {
            Task? pendingTransition = null;
            TeardownTransition? transition = null;
            long generation = 0;
            await _lifecycle.WaitAsync();
            try
            {
                if (_disposed)
                {
                    pendingTransition = _teardownCompletion;
                }
                else if (State is VisualCaptureState.Pausing or VisualCaptureState.Stopping || !_teardownCompletion.IsCompleted)
                {
                    pendingTransition = _teardownCompletion;
                }
                else
                {
                    _disposed = true;
                    generation = ++_generation;
                    transition = BeginTeardown(TakeCancellation(), TakeCapture(), _startCompletion);
                    _authorized = false;
                    SetState(VisualCaptureState.Stopping);
                }
            }
            finally
            {
                _lifecycle.Release();
            }

            if (pendingTransition is not null)
            {
                await pendingTransition;
                if (_disposed) return;
                continue;
            }

            await ExecuteTeardownAsync(transition!, generation, VisualCaptureState.Stopping, VisualCaptureState.Stopped);
            return;
        }
    }

    private async ValueTask<VisualCaptureState> StartPreparedCaptureAsync(
        long generation,
        CancellationTokenSource captureCancellation,
        TaskCompletionSource startCompletion)
    {
        IVisualCaptureLease? startedCapture = null;
        try
        {
            startedCapture = await _capture.StartValidatedAsync(captureCancellation.Token);
            if (startedCapture.Completion.IsCompleted)
            {
                var exit = await ReadCompletedExitAsync(startedCapture);
                var completedCapture = startedCapture;
                startedCapture = null;
                return await NormalizeStartFailureAsync(generation, MapExit(exit.Reason), completedCapture);
            }

            await _lifecycle.WaitAsync();
            try
            {
                if (_disposed || generation != _generation || State != VisualCaptureState.Starting) return State;

                _activeCapture = startedCapture;
                SetState(VisualCaptureState.Active);
                _ = ObserveCompletionAsync(startedCapture, generation, captureCancellation.Token);
                startedCapture = null;
                return State;
            }
            finally
            {
                _lifecycle.Release();
            }
        }
        catch (OperationCanceledException) when (captureCancellation.IsCancellationRequested)
        {
            return await NormalizeStartFailureAsync(generation, VisualCaptureState.Paused);
        }
        catch
        {
            return await NormalizeStartFailureAsync(generation, VisualCaptureState.Failed);
        }
        finally
        {
            if (startedCapture is not null) await DisposeCaptureSafelyAsync(startedCapture);
            startCompletion.TrySetResult();
        }
    }

    private static async Task<VisualCaptureExit> ReadCompletedExitAsync(IVisualCaptureLease capture)
    {
        try
        {
            return await capture.Completion;
        }
        catch (OperationCanceledException)
        {
            return new(VisualCaptureExitReason.Cancelled);
        }
        catch
        {
            return new(VisualCaptureExitReason.UnexpectedFailure);
        }
    }

    private async Task ObserveCompletionAsync(
        IVisualCaptureLease capture,
        long generation,
        CancellationToken cancellationToken)
    {
        VisualCaptureExit exit;
        try
        {
            exit = await capture.Completion.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            exit = new(VisualCaptureExitReason.Cancelled);
        }
        catch
        {
            exit = new(VisualCaptureExitReason.UnexpectedFailure);
        }

        TeardownTransition? transition = null;
        VisualCaptureState finalState = default;
        VisualCaptureState transientState = default;
        long transitionGeneration = 0;
        await _lifecycle.WaitAsync();
        try
        {
            if (_disposed || generation != _generation || !ReferenceEquals(_activeCapture, capture)) return;

            transitionGeneration = ++_generation;
            finalState = MapExit(exit.Reason);
            transientState = finalState == VisualCaptureState.Paused
                ? VisualCaptureState.Pausing
                : VisualCaptureState.Stopping;
            transition = BeginTeardown(TakeCancellation(), TakeCapture(), _startCompletion);
            if (finalState == VisualCaptureState.Stopped) _authorized = false;
            SetState(transientState);
        }
        finally
        {
            _lifecycle.Release();
        }

        await ExecuteTeardownAsync(transition, transitionGeneration, transientState, finalState);
    }

    private async ValueTask<VisualCaptureState> NormalizeStartFailureAsync(
        long generation,
        VisualCaptureState failureState,
        IVisualCaptureLease? completedCapture = null)
    {
        TeardownTransition? transition = null;
        long transitionGeneration = 0;
        await _lifecycle.WaitAsync();
        try
        {
            if (!_disposed && generation == _generation)
            {
                transitionGeneration = ++_generation;
                transition = BeginTeardown(TakeCancellation(), completedCapture, Task.CompletedTask);
                SetState(VisualCaptureState.Stopping);
            }
        }
        finally
        {
            _lifecycle.Release();
        }

        if (transition is not null)
        {
            await ExecuteTeardownAsync(
                transition,
                transitionGeneration,
                VisualCaptureState.Stopping,
                failureState);
        }
        else if (completedCapture is not null)
        {
            await DisposeCaptureSafelyAsync(completedCapture);
        }
        return State;
    }

    private TeardownTransition BeginTeardown(
        CancellationTokenSource? cancellation,
        IVisualCaptureLease? capture,
        Task startCompletion)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transition = new TeardownTransition(
            _teardownCompletion,
            startCompletion,
            cancellation,
            capture,
            completion);
        _teardownCompletion = completion.Task;
        return transition;
    }

    private async Task ExecuteTeardownAsync(
        TeardownTransition transition,
        long generation,
        VisualCaptureState transientState,
        VisualCaptureState finalState)
    {
        try
        {
            await transition.PreviousCompletion;
            await CancelSafelyAsync(transition.Cancellation);
            if (transition.Capture is not null) await DisposeCaptureSafelyAsync(transition.Capture);
            await transition.StartCompletion;

            await _lifecycle.WaitAsync();
            try
            {
                if (generation == _generation && State == transientState) SetState(finalState);
            }
            finally
            {
                _lifecycle.Release();
            }
        }
        finally
        {
            transition.Cancellation?.Dispose();
            transition.Completion.TrySetResult();
        }
    }

    private IVisualCaptureLease? TakeCapture()
    {
        var capture = _activeCapture;
        _activeCapture = null;
        return capture;
    }

    private CancellationTokenSource? TakeCancellation()
    {
        var cancellation = _captureCancellation;
        _captureCancellation = null;
        return cancellation;
    }

    private void SetState(VisualCaptureState state) => Volatile.Write(ref _state, (int)state);

    private static VisualCaptureState MapExit(VisualCaptureExitReason reason) => reason switch
    {
        VisualCaptureExitReason.Completed => VisualCaptureState.Stopped,
        VisualCaptureExitReason.Cancelled => VisualCaptureState.Paused,
        VisualCaptureExitReason.Minimized => VisualCaptureState.Paused,
        VisualCaptureExitReason.TargetLost => VisualCaptureState.TargetUnavailable,
        VisualCaptureExitReason.ProtectedContent => VisualCaptureState.ProtectedContent,
        VisualCaptureExitReason.NotSupported => VisualCaptureState.NotSupported,
        VisualCaptureExitReason.DeviceLost => VisualCaptureState.Failed,
        _ => VisualCaptureState.Failed
    };

    private static async ValueTask CancelSafelyAsync(CancellationTokenSource? cancellation)
    {
        if (cancellation is null) return;
        try
        {
            await cancellation.CancelAsync();
        }
        catch
        {
            // Capture cancellation is isolated from the recording pipeline.
        }
    }

    private static async ValueTask DisposeCaptureSafelyAsync(IVisualCaptureLease capture)
    {
        try
        {
            await capture.DisposeAsync();
        }
        catch
        {
            // Capture cleanup failures are normalized and remain visual-only.
        }
    }

    private sealed record TeardownTransition(
        Task PreviousCompletion,
        Task StartCompletion,
        CancellationTokenSource? Cancellation,
        IVisualCaptureLease? Capture,
        TaskCompletionSource Completion);
}
