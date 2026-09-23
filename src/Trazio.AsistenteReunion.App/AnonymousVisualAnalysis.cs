using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.App;

internal enum AnonymousVisualAnalysisScope
{
    ActivityAndAvailabilityIntervals = 1
}

internal sealed class AnonymousVisualAnalysisAuthorization
{
    public const int Current = 1;
    public const int CurrentVersion = Current;

    private readonly MeetingWindowSelection _selection;
    private readonly string _sessionId;
    private readonly AnonymousVisualAnalysisScope _scope;
    private int _consumed;

    private AnonymousVisualAnalysisAuthorization(
        MeetingWindowSelection selection,
        string sessionId,
        AnonymousVisualAnalysisScope scope)
    {
        _selection = selection;
        _sessionId = sessionId;
        _scope = scope;
        AuthorizationId = Guid.NewGuid();
    }

    public Guid AuthorizationId { get; }
    public int Version => Current;
    public AnonymousVisualAnalysisScope Scope => _scope;

    public static AnonymousVisualAnalysisAuthorization GrantForSession(
        MeetingWindowSelection selection,
        string sessionId,
        AnonymousVisualAnalysisScope scope = AnonymousVisualAnalysisScope.ActivityAndAvailabilityIntervals)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (!Enum.IsDefined(scope)) throw new ArgumentOutOfRangeException(nameof(scope));
        return new(selection, sessionId, scope);
    }

    public bool Matches(
        MeetingWindowSelection? selection,
        string? sessionId,
        AnonymousVisualAnalysisScope scope = AnonymousVisualAnalysisScope.ActivityAndAvailabilityIntervals) =>
        selection is not null &&
        selection == _selection &&
        string.Equals(sessionId, _sessionId, StringComparison.Ordinal) &&
        scope == _scope &&
        Version == Current &&
        Volatile.Read(ref _consumed) == 0;

    public bool TryConsume(
        MeetingWindowSelection selection,
        string sessionId,
        AnonymousVisualAnalysisScope scope = AnonymousVisualAnalysisScope.ActivityAndAvailabilityIntervals)
    {
        if (!Matches(selection, sessionId, scope)) return false;
        return Interlocked.CompareExchange(ref _consumed, 1, 0) == 0;
    }
}

internal enum AnonymousVisualAnalysisActivationFailure
{
    None = 0,
    SystemOutputRequired = 1,
    SelectionRequired = 2,
    ProviderUnsupported = 3,
    SessionUnavailable = 4,
    TimelineMismatch = 5,
    StoreUnavailable = 6,
    AuthorizationUnavailable = 7,
    ComponentCreationFailed = 8
}

internal enum VisualProbeFailureKind
{
    Extraction = 0,
    Detector = 1,
    Sink = 2
}

internal interface IAnonymousVisualAnalysisSession : IVisualProbeFrameConsumer, IAsyncDisposable
{
    long DetectorFailureCount { get; }
    long SinkFailureCount { get; }
    long ExtractionFailureCount { get; }
    ValueTask CompleteAt(TimeSpan finalOffset);
}

internal interface IAnonymousVisualAnalysisComponentFactory
{
    IVisualProbeEvidenceSink CreateSink(string sessionId, SqliteSessionStore store);
    IVisualProbeDetector CreateDetector(string sessionId);
    IAnonymousVisualAnalysisSession CreateSession(
        ISessionTimelineContext timelineContext,
        MeetingProvider provider,
        IVisualProbeDetector detector,
        IVisualProbeEvidenceSink sink);
}

internal sealed class AnonymousVisualAnalysisComponentFactory(
    Action<VisualProbeFailureKind>? failureObserver = null,
    Action<string>? evidencePersisted = null)
    : IAnonymousVisualAnalysisComponentFactory
{
    public IVisualProbeEvidenceSink CreateSink(string sessionId, SqliteSessionStore store) =>
        new SqliteVisualProbeEvidenceSink(sessionId, store, evidencePersisted);

    public IVisualProbeDetector CreateDetector(string sessionId) =>
        new DeterministicVisualActivityDetector(sessionId);

    public IAnonymousVisualAnalysisSession CreateSession(
        ISessionTimelineContext timelineContext,
        MeetingProvider provider,
        IVisualProbeDetector detector,
        IVisualProbeEvidenceSink sink) =>
        VisualProbeSession.Start(
            timelineContext,
            VisualProbeProfiles.GetProduction(provider),
            new D3D11VisualProbeExtractor(),
            detector,
            sink,
            failureObserver: failureObserver);
}

internal readonly record struct AnonymousVisualAnalysisActivationResult(
    IAnonymousVisualAnalysisSession? Session,
    VisualProbeProfileValidationState? ProfileValidationState,
    AnonymousVisualAnalysisActivationFailure Failure)
{
    public bool IsActivated => Session is not null && Failure == AnonymousVisualAnalysisActivationFailure.None;
}

internal static class AnonymousVisualAnalysisActivator
{
    public static AnonymousVisualAnalysisActivationResult TryCreate(
        bool captureSystemOutput,
        MeetingWindowSelection? selection,
        string? activeSessionId,
        ISessionTimelineContext? activeTimelineContext,
        SqliteSessionStore? store,
        AnonymousVisualAnalysisAuthorization? authorization,
        IAnonymousVisualAnalysisComponentFactory componentFactory)
    {
        ArgumentNullException.ThrowIfNull(componentFactory);

        if (!captureSystemOutput)
            return Failed(AnonymousVisualAnalysisActivationFailure.SystemOutputRequired);
        if (selection is null)
            return Failed(AnonymousVisualAnalysisActivationFailure.SelectionRequired);
        if (selection.Provider is not (MeetingProvider.GoogleMeet or MeetingProvider.MicrosoftTeams))
            return Failed(AnonymousVisualAnalysisActivationFailure.ProviderUnsupported);
        if (string.IsNullOrWhiteSpace(activeSessionId))
            return Failed(AnonymousVisualAnalysisActivationFailure.SessionUnavailable);
        if (activeTimelineContext is null ||
            !string.Equals(activeTimelineContext.SessionId, activeSessionId, StringComparison.Ordinal) ||
            !activeTimelineContext.TryGetCurrentOffset(out _))
            return Failed(AnonymousVisualAnalysisActivationFailure.TimelineMismatch);
        if (store is null)
            return Failed(AnonymousVisualAnalysisActivationFailure.StoreUnavailable);
        if (authorization is null ||
            !authorization.TryConsume(
                selection,
                activeSessionId,
                AnonymousVisualAnalysisScope.ActivityAndAvailabilityIntervals))
            return Failed(AnonymousVisualAnalysisActivationFailure.AuthorizationUnavailable);

        try
        {
            var sink = componentFactory.CreateSink(activeSessionId, store);
            var detector = componentFactory.CreateDetector(activeSessionId);
            var session = componentFactory.CreateSession(
                activeTimelineContext,
                selection.Provider,
                detector,
                sink);
            return new(
                session,
                VisualProbeProfiles.GetProduction(selection.Provider).ValidationState,
                AnonymousVisualAnalysisActivationFailure.None);
        }
        catch
        {
            return Failed(AnonymousVisualAnalysisActivationFailure.ComponentCreationFailed);
        }
    }

    private static AnonymousVisualAnalysisActivationResult Failed(
        AnonymousVisualAnalysisActivationFailure failure) =>
        new(null, null, failure);
}
