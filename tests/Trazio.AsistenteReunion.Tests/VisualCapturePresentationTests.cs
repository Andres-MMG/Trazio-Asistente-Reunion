using Trazio.AsistenteReunion.App;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.Tests;

public sealed class VisualCapturePresentationTests
{
    [Fact]
    [Trait("Area", "VisualCapture")]
    public void Authorization_IsBoundToExactSelectionAndConsumedOnce()
    {
        var selected = new MeetingWindowSelection((nint)42, 7, MeetingProvider.GoogleMeet);
        var changed = selected with { ProcessId = 8 };
        var sessionId = Guid.NewGuid();
        var authorization = VisualCaptureAuthorization.GrantForSelection(selected);

        Assert.NotEqual(Guid.Empty, authorization.AuthorizationId);
        Assert.True(authorization.Matches(selected));
        Assert.False(authorization.Matches(changed));
        Assert.False(authorization.TryConsume(changed, sessionId, out _));
        Assert.True(authorization.TryConsume(selected, sessionId, out var consent));
        Assert.NotNull(consent);
        Assert.False(authorization.Matches(selected));
        Assert.False(authorization.TryConsume(selected, sessionId, out _));
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void Presenter_RequiresSelectionAndSeparateAuthorization()
    {
        var withoutSelection = VisualCapturePresenter.Create(
            VisualCaptureState.Off, false, false, true, false, false);
        var selectable = VisualCapturePresenter.Create(
            VisualCaptureState.Off, true, false, true, false, false);
        var authorized = VisualCapturePresenter.Create(
            VisualCaptureState.Off, true, true, true, false, false);

        Assert.False(withoutSelection.CanAuthorize);
        Assert.Contains("Selecciona una ventana", withoutSelection.Status);
        Assert.True(selectable.CanAuthorize);
        Assert.Contains("no lo activa", selectable.Status);
        Assert.False(authorized.CanAuthorize);
        Assert.Contains("autorizado", authorized.Status);
    }

    [Theory]
    [InlineData(VisualCaptureState.Active, true, false, true, true, true)]
    [InlineData(VisualCaptureState.Paused, false, true, true, false, true)]
    [InlineData(VisualCaptureState.TargetMinimized, false, true, true, false, true)]
    [InlineData(VisualCaptureState.Stopped, false, false, false, false, false)]
    [InlineData(VisualCaptureState.NotSupported, false, false, false, false, false)]
    [InlineData(VisualCaptureState.TargetUnavailable, false, false, false, false, false)]
    [InlineData(VisualCaptureState.ProtectedContent, false, false, false, false, false)]
    [InlineData(VisualCaptureState.Failed, false, false, false, false, false)]
    [Trait("Area", "VisualCapture")]
    public void Presenter_ExposesIndependentVisualControls(
        VisualCaptureState state,
        bool canPause,
        bool canResume,
        bool canStop,
        bool showActiveIndicator,
        bool showStop)
    {
        var presentation = VisualCapturePresenter.Create(state, true, false, true, false, false);

        Assert.Equal(canPause, presentation.CanPause);
        Assert.Equal(canResume, presentation.CanResume);
        Assert.Equal(canStop, presentation.CanStop);
        Assert.Equal(showActiveIndicator, presentation.ShowActiveIndicator);
        Assert.Equal(showStop, presentation.ShowStop);
        if (state == VisualCaptureState.Active)
            Assert.Equal("Análisis visual activo · no se guardan imágenes", presentation.Status);
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void PausedRecording_CannotAuthorizeOrStartVisualCapture()
    {
        var presentation = VisualCapturePresenter.Create(
            VisualCaptureState.Off,
            hasValidSelection: true,
            hasPendingAuthorization: false,
            isReady: true,
            isRecordingPaused: true,
            actionInProgress: false);

        Assert.False(presentation.CanAuthorize);
        Assert.False(VisualCaptureActivationPolicy.CanStart(true, true, false));
        Assert.True(VisualCaptureActivationPolicy.CanStart(true, false, false));
        Assert.False(VisualCaptureActivationPolicy.CanStart(true, false, true));
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public async Task Shutdown_StopsVisualBeforeAudioAndContinuesWhenVisualCleanupFails()
    {
        var ordered = new List<string>();
        await VisualCaptureShutdown.RunVisualFirstAsync(
            () =>
            {
                ordered.Add("visual");
                return Task.CompletedTask;
            },
            () =>
            {
                ordered.Add("audio");
                return Task.CompletedTask;
            });

        Assert.Equal(["visual", "audio"], ordered);

        var audioStopped = false;
        await VisualCaptureShutdown.RunVisualFirstAsync(
            () => throw new InvalidOperationException("visual teardown failed"),
            () =>
            {
                audioStopped = true;
                return Task.CompletedTask;
            });
        Assert.True(audioStopped);
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void StateTracker_RejectsCallbacksFromOldControllerAndOldRevision()
    {
        var current = Guid.NewGuid();
        var tracker = new VisualCaptureStateTracker();
        tracker.Reset(current);

        Assert.False(tracker.TryAccept(new(Guid.NewGuid(), 1, VisualCaptureState.Failed)));
        Assert.True(tracker.TryAccept(new(current, 2, VisualCaptureState.Active)));
        Assert.False(tracker.TryAccept(new(current, 1, VisualCaptureState.Failed)));
        Assert.False(tracker.TryAccept(new(current, 2, VisualCaptureState.Stopped)));
        Assert.True(tracker.TryAccept(new(current, 3, VisualCaptureState.Paused)));
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void WeakObserver_ForwardsStateWithoutOwningACallbackDelegate()
    {
        var target = new ObserverTarget();
        var observer = new WeakVisualCaptureStateObserver<ObserverTarget>(target);
        observer.OnStateChanged(new(Guid.NewGuid(), 1, VisualCaptureState.Active));
        Assert.Equal(1, target.Calls);
    }

    private sealed class ObserverTarget : IVisualCaptureStateSink
    {
        public int Calls { get; set; }

        public void ReceiveVisualCaptureState(VisualCaptureStateChange change) => Calls++;
    }
}
