using Trazio.AsistenteReunion.App;
using Trazio.AsistenteReunion.Core;
using Windows.Graphics.DirectX.Direct3D11;

namespace Trazio.AsistenteReunion.Tests;

public sealed class AnonymousVisualAnalysisActivationTests
{
    private const string SessionId = "0123456789abcdef0123456789abcdef";
    private static readonly MeetingWindowSelection Selection =
        new((nint)42, 7, MeetingProvider.GoogleMeet);

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void Authorization_IsCurrentBoundToSelectionSessionAndScopeAndConsumedOnce()
    {
        var authorization = AnonymousVisualAnalysisAuthorization.GrantForSession(
            Selection,
            SessionId,
            AnonymousVisualAnalysisScope.ActivityAndAvailabilityIntervals);

        Assert.Equal(1, AnonymousVisualAnalysisAuthorization.Current);
        Assert.Equal(AnonymousVisualAnalysisAuthorization.Current, authorization.Version);
        Assert.NotEqual(Guid.Empty, authorization.AuthorizationId);
        Assert.Equal(AnonymousVisualAnalysisScope.ActivityAndAvailabilityIntervals, authorization.Scope);
        Assert.True(authorization.Matches(Selection, SessionId));
        Assert.False(authorization.Matches(Selection with { Handle = (nint)43 }, SessionId));
        Assert.False(authorization.Matches(Selection with { ProcessId = 8 }, SessionId));
        Assert.False(authorization.Matches(
            Selection with { Provider = MeetingProvider.MicrosoftTeams },
            SessionId));
        Assert.False(authorization.Matches(Selection, "fedcba9876543210fedcba9876543210"));
        Assert.False(authorization.Matches(Selection, SessionId, (AnonymousVisualAnalysisScope)999));
        Assert.False(authorization.TryConsume(Selection, SessionId, (AnonymousVisualAnalysisScope)999));
        Assert.True(authorization.TryConsume(Selection, SessionId));
        Assert.False(authorization.Matches(Selection, SessionId));
        Assert.False(authorization.TryConsume(Selection, SessionId));
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void TryCreate_WithoutSystemOutputCreatesNoVisualAnalysisComponents()
    {
        var factory = new RecordingComponentFactory();
        var authorization = AnonymousVisualAnalysisAuthorization.GrantForSession(Selection, SessionId);

        var result = AnonymousVisualAnalysisActivator.TryCreate(
            captureSystemOutput: false,
            Selection,
            SessionId,
            new FakeTimelineContext(SessionId),
            store: null,
            authorization,
            factory);

        Assert.Equal(AnonymousVisualAnalysisActivationFailure.SystemOutputRequired, result.Failure);
        Assert.Null(result.Session);
        Assert.Empty(factory.Calls);
        Assert.True(authorization.Matches(Selection, SessionId));
    }

    [Theory]
    [InlineData(true, "fedcba9876543210fedcba9876543210")]
    [InlineData(false, SessionId)]
    [Trait("Area", "VisualCapture")]
    public void TryCreate_WithMismatchedOrStaleTimelineFailsClosedBeforeConstruction(
        bool timelineActive,
        string timelineSessionId)
    {
        var factory = new RecordingComponentFactory();
        var authorization = AnonymousVisualAnalysisAuthorization.GrantForSession(Selection, SessionId);

        var result = AnonymousVisualAnalysisActivator.TryCreate(
            captureSystemOutput: true,
            Selection,
            SessionId,
            new FakeTimelineContext(timelineSessionId, timelineActive),
            store: null,
            authorization,
            factory);

        Assert.Equal(AnonymousVisualAnalysisActivationFailure.TimelineMismatch, result.Failure);
        Assert.Null(result.Session);
        Assert.Empty(factory.Calls);
        Assert.True(authorization.Matches(Selection, SessionId));
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void TryCreate_ConsumesAuthorizationThenConstructsSinkDetectorAndSessionInOrder()
    {
        using var protector = new AesContentProtector(new byte[32]);
        var store = new SqliteSessionStore(Path.Combine(Path.GetTempPath(), "unused-visual-analysis.db"), protector);
        var factory = new RecordingComponentFactory();
        var authorization = AnonymousVisualAnalysisAuthorization.GrantForSession(Selection, SessionId);

        var result = AnonymousVisualAnalysisActivator.TryCreate(
            captureSystemOutput: true,
            Selection,
            SessionId,
            new FakeTimelineContext(SessionId),
            store,
            authorization,
            factory);

        Assert.True(result.IsActivated);
        Assert.Same(factory.Session, result.Session);
        Assert.Equal(VisualProbeProfileValidationState.Unvalidated, result.ProfileValidationState);
        Assert.Equal(["sink", "detector", "session"], factory.Calls);
        Assert.False(authorization.Matches(Selection, SessionId));
        Assert.False(authorization.TryConsume(Selection, SessionId));
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void Presenter_NeverClaimsActiveForCurrentUnvalidatedProfile()
    {
        var state = AnonymousVisualAnalysisPresenter.Create(
            AnonymousVisualAnalysisStatus.ProfileUnavailable,
            hasValidSelection: true,
            MeetingProvider.GoogleMeet,
            isRecording: true,
            captureSystemOutput: true,
            VisualCaptureState.Active,
            isReady: true,
            isRecordingPaused: false,
            actionInProgress: false);

        Assert.Equal("Evidencia visual no disponible: perfil en validación.", state.Status);
        Assert.DoesNotContain("activo", state.Status, StringComparison.OrdinalIgnoreCase);
        Assert.False(state.CanAuthorize);
    }

    private sealed class RecordingComponentFactory : IAnonymousVisualAnalysisComponentFactory
    {
        public List<string> Calls { get; } = [];
        public FakeAnalysisSession Session { get; } = new();

        public IVisualProbeEvidenceSink CreateSink(string sessionId, SqliteSessionStore store)
        {
            Calls.Add("sink");
            return new NoOpSink();
        }

        public IVisualProbeDetector CreateDetector(string sessionId)
        {
            Calls.Add("detector");
            return new NoOpDetector();
        }

        public IAnonymousVisualAnalysisSession CreateSession(
            ISessionTimelineContext timelineContext,
            MeetingProvider provider,
            IVisualProbeDetector detector,
            IVisualProbeEvidenceSink sink)
        {
            Calls.Add("session");
            return Session;
        }
    }

    private sealed class FakeTimelineContext(string sessionId, bool active = true) : ISessionTimelineContext
    {
        public string SessionId { get; } = sessionId;
        public long Revision => 1;
        public DateTimeOffset StartedAtUtc => DateTimeOffset.UnixEpoch;

        public bool TryGetCurrentOffset(out TimeSpan offset)
        {
            offset = TimeSpan.Zero;
            return active;
        }
    }

    private sealed class FakeAnalysisSession : IAnonymousVisualAnalysisSession
    {
        public long DetectorFailureCount => 0;
        public long SinkFailureCount => 0;
        public long ExtractionFailureCount => 0;
        public bool BeginSurface(out long surfaceRevision)
        {
            surfaceRevision = 1;
            return true;
        }

        public VisualProbeFrameResult ObserveFrame(long surfaceRevision, Func<IDirect3DSurface> getSurface) =>
            VisualProbeFrameResult.ProfileUnavailable;

        public bool MarkUnavailable(long surfaceRevision) => true;
        public ValueTask CompleteAt(TimeSpan finalOffset) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NoOpSink : IVisualProbeEvidenceSink
    {
        public ValueTask WriteAsync(
            AnonymousVisualEvidenceInterval interval,
            CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    private sealed class NoOpDetector : IVisualProbeDetector
    {
        public VisualProbeDetectionResult Observe(
            VisualProbeObservation observation,
            ReadOnlySpan<VisualProbeFeature> features) =>
            VisualProbeDetectionResult.Observed([]);
    }
}
