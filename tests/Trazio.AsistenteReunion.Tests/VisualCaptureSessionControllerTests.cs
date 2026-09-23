using Trazio.AsistenteReunion.App;

namespace Trazio.AsistenteReunion.Tests;

public sealed class VisualCaptureSessionControllerTests
{
    [Fact]
    [Trait("Area", "VisualCapture")]
    public void NewSession_DefaultsToOffWithoutStartingCapture()
    {
        var capture = new FakeVisualMeetingCapture();
        var controller = new VisualCaptureSessionController(Guid.NewGuid(), capture);

        Assert.Equal(VisualCaptureState.Off, controller.State);
        Assert.Equal(0, capture.StartCount);
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public async Task StartAsync_ConsentIsConsumedAndCannotBeInheritedByAnotherSession()
    {
        var firstSession = Guid.NewGuid();
        var consent = VisualCaptureConsent.GrantForSession(firstSession);
        var firstCapture = new FakeVisualMeetingCapture();
        await using var first = new VisualCaptureSessionController(firstSession, firstCapture);

        await first.StartAsync(consent);

        var secondCapture = new FakeVisualMeetingCapture();
        await using var second = new VisualCaptureSessionController(firstSession, secondCapture);
        await second.StartAsync(consent);

        Assert.Equal(VisualCaptureState.Active, first.State);
        Assert.Equal(1, firstCapture.StartCount);
        Assert.Equal(VisualCaptureState.Off, second.State);
        Assert.Equal(0, secondCapture.StartCount);
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public async Task StartAsync_ConsentForDifferentSession_IsRejectedWithoutBeingConsumed()
    {
        var authorizedSession = Guid.NewGuid();
        var consent = VisualCaptureConsent.GrantForSession(authorizedSession);
        await using var wrongSession = new VisualCaptureSessionController(Guid.NewGuid(), new FakeVisualMeetingCapture());

        await wrongSession.StartAsync(consent);

        var capture = new FakeVisualMeetingCapture();
        await using var correctSession = new VisualCaptureSessionController(authorizedSession, capture);
        await correctSession.StartAsync(consent);

        Assert.Equal(VisualCaptureState.Off, wrongSession.State);
        Assert.Equal(VisualCaptureState.Active, correctSession.State);
        Assert.Equal(1, capture.StartCount);
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public async Task ActiveCapture_WhenSessionCancellationFires_PausesAndDisposesCapture()
    {
        var sessionId = Guid.NewGuid();
        var capture = new FakeVisualMeetingCapture();
        using var cancellation = new CancellationTokenSource();
        await using var controller = new VisualCaptureSessionController(sessionId, capture);
        await controller.StartAsync(VisualCaptureConsent.GrantForSession(sessionId), cancellation.Token);
        var lease = capture.Leases.Single();

        cancellation.Cancel();
        await WaitUntilAsync(() => controller.State == VisualCaptureState.Paused);

        Assert.Equal(1, lease.DisposeCount);
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public async Task Lifecycle_StartPauseResumeStop_IsIdempotentAndRevalidatesOnResume()
    {
        var sessionId = Guid.NewGuid();
        var capture = new FakeVisualMeetingCapture();
        await using var controller = new VisualCaptureSessionController(sessionId, capture);

        await controller.StartAsync(VisualCaptureConsent.GrantForSession(sessionId));
        await controller.StartAsync(VisualCaptureConsent.GrantForSession(sessionId));
        await controller.PauseAsync();
        await controller.PauseAsync();
        await controller.ResumeAsync();
        await controller.ResumeAsync();
        await controller.StopAsync();
        await controller.StopAsync();

        Assert.Equal(2, capture.StartCount);
        Assert.All(capture.Leases, lease => Assert.Equal(1, lease.DisposeCount));
        Assert.Equal(VisualCaptureState.Stopped, controller.State);
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public async Task ResumeAsync_WhilePriorLeaseIsDisposing_WaitsBeforeStartingNewCapture()
    {
        var sessionId = Guid.NewGuid();
        var capture = new FakeVisualMeetingCapture();
        await using var controller = new VisualCaptureSessionController(sessionId, capture);
        await controller.StartAsync(VisualCaptureConsent.GrantForSession(sessionId));
        var firstLease = capture.Leases.Single();
        firstLease.BlockDisposal();

        var pausing = controller.PauseAsync().AsTask();
        await firstLease.DisposeStarted;
        var resuming = controller.ResumeAsync().AsTask();

        Assert.Equal(VisualCaptureState.Pausing, controller.State);
        Assert.False(pausing.IsCompleted);
        Assert.False(resuming.IsCompleted);
        Assert.Equal(1, capture.StartCount);

        firstLease.ReleaseDisposal();
        await pausing;
        await resuming;

        Assert.Equal(VisualCaptureState.Active, controller.State);
        Assert.Equal(2, capture.StartCount);
        Assert.Equal(1, firstLease.DisposeCount);
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public async Task StartAsync_WhilePausing_WaitsForPriorTeardownBeforeStarting()
    {
        var sessionId = Guid.NewGuid();
        var capture = new FakeVisualMeetingCapture();
        await using var controller = new VisualCaptureSessionController(sessionId, capture);
        await controller.StartAsync(VisualCaptureConsent.GrantForSession(sessionId));
        var firstLease = capture.Leases.Single();
        firstLease.BlockDisposal();
        var pausing = controller.PauseAsync().AsTask();
        await firstLease.DisposeStarted;

        var starting = controller.StartAsync(VisualCaptureConsent.GrantForSession(sessionId)).AsTask();

        Assert.False(starting.IsCompleted);
        Assert.Equal(1, capture.StartCount);
        firstLease.ReleaseDisposal();
        await pausing;
        await starting;

        Assert.Equal(VisualCaptureState.Active, controller.State);
        Assert.Equal(2, capture.StartCount);
        Assert.Equal(1, firstLease.DisposeCount);
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public async Task StartAsync_WhileStopping_DoesNotRestartCapture()
    {
        var sessionId = Guid.NewGuid();
        var capture = new FakeVisualMeetingCapture();
        await using var controller = new VisualCaptureSessionController(sessionId, capture);
        await controller.StartAsync(VisualCaptureConsent.GrantForSession(sessionId));
        var firstLease = capture.Leases.Single();
        firstLease.BlockDisposal();
        var stopping = controller.StopAsync().AsTask();
        await firstLease.DisposeStarted;
        var unusedConsent = VisualCaptureConsent.GrantForSession(sessionId);

        var starting = controller.StartAsync(unusedConsent).AsTask();

        Assert.False(starting.IsCompleted);
        Assert.Equal(1, capture.StartCount);
        firstLease.ReleaseDisposal();
        await stopping;
        await starting;

        Assert.Equal(VisualCaptureState.Stopped, controller.State);
        Assert.Equal(1, capture.StartCount);

        var nextCapture = new FakeVisualMeetingCapture();
        await using var nextController = new VisualCaptureSessionController(sessionId, nextCapture);
        await nextController.StartAsync(unusedConsent);
        Assert.Equal(VisualCaptureState.Active, nextController.State);
        Assert.Equal(1, nextCapture.StartCount);
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public async Task StartAsync_WithPrecompletedLease_WaitsForItsTeardownBeforeStartingAgain()
    {
        var sessionId = Guid.NewGuid();
        var capture = new FakeVisualMeetingCapture();
        var firstLease = new FakeVisualCaptureLease();
        firstLease.BlockDisposal();
        firstLease.Complete(VisualCaptureExitReason.Minimized);
        capture.QueueLease(firstLease);
        await using var controller = new VisualCaptureSessionController(sessionId, capture);

        var firstStart = controller.StartAsync(VisualCaptureConsent.GrantForSession(sessionId)).AsTask();
        await firstLease.DisposeStarted;
        var secondStart = controller.StartAsync(VisualCaptureConsent.GrantForSession(sessionId)).AsTask();

        Assert.False(firstStart.IsCompleted);
        Assert.False(secondStart.IsCompleted);
        Assert.Equal(VisualCaptureState.Stopping, controller.State);
        Assert.Equal(1, capture.StartCount);

        firstLease.ReleaseDisposal();
        await firstStart;
        await secondStart;

        Assert.Equal(VisualCaptureState.Active, controller.State);
        Assert.Equal(2, capture.StartCount);
        Assert.Equal(1, firstLease.DisposeCount);
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public async Task StartAsync_WhenVisualCaptureThrows_NormalizesFailureWithoutPropagating()
    {
        var sessionId = Guid.NewGuid();
        var capture = new FakeVisualMeetingCapture { StartException = new InvalidOperationException("sensitive detail") };
        await using var controller = new VisualCaptureSessionController(sessionId, capture);

        var state = await controller.StartAsync(VisualCaptureConsent.GrantForSession(sessionId));

        Assert.Equal(VisualCaptureState.Failed, state);
        Assert.Equal(VisualCaptureState.Failed, controller.State);
        Assert.Equal(1, capture.StartCount);
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public async Task CompletionFromSupersededCapture_CannotChangeCurrentState()
    {
        var sessionId = Guid.NewGuid();
        var capture = new FakeVisualMeetingCapture();
        await using var controller = new VisualCaptureSessionController(sessionId, capture);
        await controller.StartAsync(VisualCaptureConsent.GrantForSession(sessionId));
        var firstLease = capture.Leases.Single();
        await controller.PauseAsync();
        await controller.ResumeAsync();

        firstLease.Complete(VisualCaptureExitReason.UnexpectedFailure);
        await Task.Delay(25);

        Assert.Equal(VisualCaptureState.Active, controller.State);
        Assert.Equal(2, capture.StartCount);
        Assert.Equal(0, capture.Leases[1].DisposeCount);
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public async Task CurrentCompletion_WithExpectedExit_NormalizesStateAndDisposesCapture()
    {
        var sessionId = Guid.NewGuid();
        var capture = new FakeVisualMeetingCapture();
        await using var controller = new VisualCaptureSessionController(sessionId, capture);
        await controller.StartAsync(VisualCaptureConsent.GrantForSession(sessionId));
        var lease = capture.Leases.Single();
        lease.BlockDisposal();

        lease.Complete(VisualCaptureExitReason.ProtectedContent);
        await lease.DisposeStarted;

        Assert.Equal(VisualCaptureState.Stopping, controller.State);
        lease.ReleaseDisposal();
        await WaitUntilAsync(() => controller.State == VisualCaptureState.ProtectedContent);

        Assert.Equal(1, lease.DisposeCount);
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public async Task StateObserver_CallbackRunsOutsideLifecycleGateAndCanPauseCapture()
    {
        var sessionId = Guid.NewGuid();
        var capture = new FakeVisualMeetingCapture();
        VisualCaptureSessionController? controller = null;
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observer = new DelegatingStateObserver(change =>
        {
            if (change.State != VisualCaptureState.Active) return;
            controller!.PauseAsync().AsTask().GetAwaiter().GetResult();
            paused.TrySetResult();
        });
        await using var ownedController = controller = new(sessionId, capture, observer);

        await controller.StartAsync(VisualCaptureConsent.GrantForSession(sessionId));
        await paused.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(VisualCaptureState.Paused, controller.State);
        Assert.Equal(1, capture.Leases.Single().DisposeCount);
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public async Task StateObserver_ReportsMinimizedTargetWithOrderedRevisions()
    {
        var changes = new List<VisualCaptureStateChange>();
        var minimized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observer = new DelegatingStateObserver(change =>
        {
            lock (changes) changes.Add(change);
            if (change.State == VisualCaptureState.TargetMinimized) minimized.TrySetResult();
        });
        var sessionId = Guid.NewGuid();
        var capture = new FakeVisualMeetingCapture();
        await using var controller = new VisualCaptureSessionController(sessionId, capture, observer);
        await controller.StartAsync(VisualCaptureConsent.GrantForSession(sessionId));

        capture.Leases.Single().Complete(VisualCaptureExitReason.Minimized);
        await minimized.Task.WaitAsync(TimeSpan.FromSeconds(2));

        VisualCaptureStateChange[] snapshot;
        lock (changes) snapshot = [.. changes];
        Assert.All(snapshot, change => Assert.Equal(controller.Identity, change.SourceId));
        Assert.Equal(snapshot.Select(change => change.Revision).Order().ToArray(), snapshot.Select(change => change.Revision).ToArray());
        Assert.Contains(snapshot, change => change.State == VisualCaptureState.Starting);
        Assert.Contains(snapshot, change => change.State == VisualCaptureState.Active);
        Assert.Contains(snapshot, change => change.State == VisualCaptureState.Pausing);
        Assert.Equal(VisualCaptureState.TargetMinimized, snapshot[^1].State);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!condition())
        {
            if (DateTime.UtcNow >= timeout) throw new TimeoutException("The expected visual capture state was not reached.");
            await Task.Delay(10);
        }
    }

    private sealed class FakeVisualMeetingCapture : IVisualMeetingCapture
    {
        private readonly Queue<FakeVisualCaptureLease> _queuedLeases = new();

        public Exception? StartException { get; set; }
        public int StartCount { get; private set; }
        public List<FakeVisualCaptureLease> Leases { get; } = [];

        public void QueueLease(FakeVisualCaptureLease lease) => _queuedLeases.Enqueue(lease);

        public ValueTask<IVisualCaptureLease> StartValidatedAsync(CancellationToken cancellationToken)
        {
            StartCount++;
            cancellationToken.ThrowIfCancellationRequested();
            if (StartException is not null) throw StartException;
            var lease = _queuedLeases.TryDequeue(out var queuedLease)
                ? queuedLease
                : new FakeVisualCaptureLease();
            Leases.Add(lease);
            return ValueTask.FromResult<IVisualCaptureLease>(lease);
        }
    }

    private sealed class DelegatingStateObserver(Action<VisualCaptureStateChange> callback)
        : IVisualCaptureStateObserver
    {
        public void OnStateChanged(VisualCaptureStateChange change) => callback(change);
    }

    private sealed class FakeVisualCaptureLease : IVisualCaptureLease
    {
        private readonly TaskCompletionSource<VisualCaptureExit> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource? _disposeStarted;
        private TaskCompletionSource? _disposeRelease;

        public Task<VisualCaptureExit> Completion => _completion.Task;
        public int DisposeCount { get; private set; }
        public Task DisposeStarted => _disposeStarted?.Task ?? Task.CompletedTask;

        public void Complete(VisualCaptureExitReason reason) => _completion.TrySetResult(new(reason));

        public void BlockDisposal()
        {
            _disposeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposeRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void ReleaseDisposal() => _disposeRelease?.TrySetResult();

        public async ValueTask DisposeAsync()
        {
            DisposeCount++;
            _disposeStarted?.TrySetResult();
            if (_disposeRelease is not null) await _disposeRelease.Task;
        }
    }
}
