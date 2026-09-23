using System.ComponentModel;
using System.Windows;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.App;

public enum AnonymousVisualEvidencePresentationState
{
    Hidden = 0,
    Analyzing = 1,
    Matched = 2,
    Insufficient = 3,
    Unavailable = 4
}

public sealed record AnonymousVisualEvidenceViewModel
{
    private const string Explanation =
        "Correlación temporal con actividad visual anónima; no identifica ni confirma la identidad de ninguna persona.";

    private AnonymousVisualEvidenceViewModel(
        AnonymousVisualEvidencePresentationState state,
        string statusText,
        bool isVisible)
    {
        State = state;
        StatusText = statusText;
        IsVisible = isVisible;
        DisplayText = isVisible ? $"Actividad visual anónima · {statusText}" : string.Empty;
        AutomationName = isVisible
            ? $"Actividad visual anónima: {statusText}. No identifica a ninguna persona."
            : string.Empty;
        HelpText = isVisible ? Explanation : string.Empty;
    }

    public AnonymousVisualEvidencePresentationState State { get; }
    public string StatusText { get; }
    public string DisplayText { get; }
    public string AutomationName { get; }
    public string HelpText { get; }
    public bool IsVisible { get; }
    public Visibility Visibility => IsVisible
        ? System.Windows.Visibility.Visible
        : System.Windows.Visibility.Collapsed;

    public static AnonymousVisualEvidenceViewModel Hidden { get; } =
        new(AnonymousVisualEvidencePresentationState.Hidden, string.Empty, false);

    public static AnonymousVisualEvidenceViewModel Analyzing { get; } =
        new(AnonymousVisualEvidencePresentationState.Analyzing, "Analizando", true);

    public static AnonymousVisualEvidenceViewModel Matched { get; } =
        new(AnonymousVisualEvidencePresentationState.Matched, "Coincidente", true);

    public static AnonymousVisualEvidenceViewModel Insufficient { get; } =
        new(AnonymousVisualEvidencePresentationState.Insufficient, "Insuficiente", true);

    public static AnonymousVisualEvidenceViewModel Unavailable { get; } =
        new(AnonymousVisualEvidencePresentationState.Unavailable, "No disponible", true);
}

public static class AnonymousVisualEvidencePresentation
{
    public static AnonymousVisualEvidenceViewModel Create(
        TranscriptSegment segment,
        AnonymousVisualCorrelation? correlation,
        bool validatedRunIncomplete = false)
    {
        ArgumentNullException.ThrowIfNull(segment);
        if (segment.Source == AudioSourceKind.Microphone)
            return AnonymousVisualEvidenceViewModel.Hidden;
        if (correlation is null)
            return AnonymousVisualEvidenceViewModel.Unavailable;

        return correlation.Outcome switch
        {
            AnonymousVisualCorrelationOutcome.Matched => AnonymousVisualEvidenceViewModel.Matched,
            AnonymousVisualCorrelationOutcome.InsufficientEvidence => AnonymousVisualEvidenceViewModel.Insufficient,
            AnonymousVisualCorrelationOutcome.Unavailable when validatedRunIncomplete =>
                AnonymousVisualEvidenceViewModel.Analyzing,
            _ => AnonymousVisualEvidenceViewModel.Unavailable
        };
    }
}

internal enum AnonymousVisualEvidenceCacheScope
{
    ActiveSession = 0,
    SelectedHistorySession = 1
}

internal sealed record AnonymousVisualEvidenceProjection(
    AnonymousVisualEvidenceCacheScope Scope,
    string SessionId,
    long Generation,
    bool IsCurrent,
    IReadOnlyDictionary<string, AnonymousVisualEvidenceViewModel> BySegmentId)
{
    public AnonymousVisualEvidenceViewModel For(TranscriptSegment segment) =>
        BySegmentId.TryGetValue(segment.Id, out var presentation)
            ? presentation
            : segment.Source == AudioSourceKind.Microphone
                ? AnonymousVisualEvidenceViewModel.Hidden
                : AnonymousVisualEvidenceViewModel.Unavailable;
}

internal sealed class AnonymousVisualEvidenceProjector
{
    private readonly Func<string, CancellationToken, Task<AnonymousVisualEvidenceReadResult>> _readEvidence;
    private readonly AnonymousVisualActivityCorrelator? _correlator;
    private readonly SnapshotSlot _active = new();
    private readonly SnapshotSlot _history = new();

    public AnonymousVisualEvidenceProjector(
        Func<string, CancellationToken, Task<AnonymousVisualEvidenceReadResult>> readEvidence,
        AnonymousVisualCorrelationPolicy? validatedPolicy = null)
    {
        _readEvidence = readEvidence ?? throw new ArgumentNullException(nameof(readEvidence));
        _correlator = validatedPolicy is null ? null : new AnonymousVisualActivityCorrelator(validatedPolicy);
    }

    public async Task<AnonymousVisualEvidenceProjection> ProjectAsync(
        AnonymousVisualEvidenceCacheScope scope,
        string sessionId,
        IReadOnlyList<TranscriptSegment> segments,
        bool validatedRunIncomplete = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(segments);

        if (segments.Any(segment =>
                !string.Equals(segment.SessionId, sessionId, StringComparison.Ordinal)))
            throw new ArgumentException("Every segment must belong to the projected session.", nameof(segments));

        var microphoneOnly = segments.All(segment => segment.Source == AudioSourceKind.Microphone);
        if (microphoneOnly)
        {
            var generation = PrepareSession(Slot(scope), sessionId);
            var hidden = segments.ToDictionary(
                segment => segment.Id,
                _ => AnonymousVisualEvidenceViewModel.Hidden,
                StringComparer.Ordinal);
            return new(scope, sessionId, generation, true, hidden);
        }

        var slot = Slot(scope);
        SnapshotLoad load;
        do
        {
            load = await GetSnapshotAsync(slot, sessionId, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        while (!IsCurrent(slot, sessionId, load.Generation));

        Func<TranscriptSegment, AnonymousVisualCorrelation>? correlate = null;
        if (!load.Failed && load.Evidence is not null && _correlator is not null)
            correlate = segment => _correlator.Correlate(segment, load.Evidence);

        var states = new Dictionary<string, AnonymousVisualEvidenceViewModel>(StringComparer.Ordinal);
        foreach (var segment in segments)
        {
            AnonymousVisualCorrelation? correlation = null;
            if (segment.Source == AudioSourceKind.SystemOutput && correlate is not null)
                correlation = correlate(segment);
            states[segment.Id] = AnonymousVisualEvidencePresentation.Create(
                segment,
                correlation,
                validatedRunIncomplete && !load.Failed && _correlator is not null);
        }

        return new(scope, sessionId, load.Generation, IsCurrent(slot, sessionId, load.Generation), states);
    }

    public void Invalidate(AnonymousVisualEvidenceCacheScope scope, string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var slot = Slot(scope);
        lock (slot.Gate)
        {
            if (!string.Equals(slot.SessionId, sessionId, StringComparison.Ordinal)) return;
            ResetSnapshot(slot, keepSession: true);
        }
    }

    public void Clear(AnonymousVisualEvidenceCacheScope scope, string? sessionId = null)
    {
        var slot = Slot(scope);
        lock (slot.Gate)
        {
            if (sessionId is not null &&
                !string.Equals(slot.SessionId, sessionId, StringComparison.Ordinal)) return;
            ResetSnapshot(slot, keepSession: false);
        }
    }

    public bool IsCurrent(AnonymousVisualEvidenceProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        return IsCurrent(Slot(projection.Scope), projection.SessionId, projection.Generation);
    }

    private async Task<SnapshotLoad> GetSnapshotAsync(
        SnapshotSlot slot,
        string sessionId,
        CancellationToken cancellationToken)
    {
        Task<SnapshotLoad> pending;
        TaskCompletionSource<SnapshotLoad>? starter = null;
        long generation;
        lock (slot.Gate)
        {
            if (!string.Equals(slot.SessionId, sessionId, StringComparison.Ordinal))
            {
                ResetSnapshot(slot, keepSession: false);
                slot.SessionId = sessionId;
            }

            generation = slot.Generation;
            if (slot.HasSnapshot)
                return slot.Snapshot!;
            if (slot.InFlight is not null)
                pending = slot.InFlight;
            else
            {
                starter = new(TaskCreationOptions.RunContinuationsAsynchronously);
                pending = starter.Task;
                slot.InFlight = pending;
            }
        }

        if (starter is not null)
            _ = CompleteLoadAsync(slot, sessionId, generation, starter);

        return await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task CompleteLoadAsync(
        SnapshotSlot slot,
        string sessionId,
        long generation,
        TaskCompletionSource<SnapshotLoad> completion)
    {
        SnapshotLoad load;
        try
        {
            var evidence = await _readEvidence(sessionId, CancellationToken.None).ConfigureAwait(false);
            load = new(generation, evidence, Failed: false);
        }
        catch
        {
            load = new(generation, null, Failed: true);
        }

        lock (slot.Gate)
        {
            if (ReferenceEquals(slot.InFlight, completion.Task))
                slot.InFlight = null;
            if (string.Equals(slot.SessionId, sessionId, StringComparison.Ordinal) &&
                slot.Generation == generation)
            {
                slot.Snapshot = load;
                slot.HasSnapshot = true;
            }
        }
        completion.TrySetResult(load);
    }

    private static long PrepareSession(SnapshotSlot slot, string sessionId)
    {
        lock (slot.Gate)
        {
            if (!string.Equals(slot.SessionId, sessionId, StringComparison.Ordinal))
            {
                ResetSnapshot(slot, keepSession: false);
                slot.SessionId = sessionId;
            }
            return slot.Generation;
        }
    }

    private static bool IsCurrent(SnapshotSlot slot, string sessionId, long generation)
    {
        lock (slot.Gate)
            return slot.Generation == generation &&
                   string.Equals(slot.SessionId, sessionId, StringComparison.Ordinal);
    }

    private SnapshotSlot Slot(AnonymousVisualEvidenceCacheScope scope) => scope switch
    {
        AnonymousVisualEvidenceCacheScope.ActiveSession => _active,
        AnonymousVisualEvidenceCacheScope.SelectedHistorySession => _history,
        _ => throw new ArgumentOutOfRangeException(nameof(scope))
    };

    private static void ResetSnapshot(SnapshotSlot slot, bool keepSession)
    {
        slot.Generation++;
        slot.HasSnapshot = false;
        slot.Snapshot = null;
        if (!keepSession) slot.SessionId = null;
    }

    private sealed class SnapshotSlot
    {
        public object Gate { get; } = new();
        public string? SessionId { get; set; }
        public long Generation { get; set; }
        public bool HasSnapshot { get; set; }
        public SnapshotLoad? Snapshot { get; set; }
        public Task<SnapshotLoad>? InFlight { get; set; }
    }

    private sealed record SnapshotLoad(
        long Generation,
        AnonymousVisualEvidenceReadResult? Evidence,
        bool Failed);
}

internal sealed class TranscriptRow : INotifyPropertyChanged
{
    private AnonymousVisualEvidenceViewModel _visualEvidence;

    public TranscriptRow(
        TranscriptSegment segment,
        AnonymousVisualEvidenceViewModel? visualEvidence = null)
    {
        Segment = segment ?? throw new ArgumentNullException(nameof(segment));
        Header = $"{segment.Start:hh\\:mm\\:ss} · {TranscriptPresentation.SpeakerLabel(segment)}";
        Text = segment.Text;
        _visualEvidence = visualEvidence ??
            (segment.Source == AudioSourceKind.Microphone
                ? AnonymousVisualEvidenceViewModel.Hidden
                : AnonymousVisualEvidenceViewModel.Unavailable);
    }

    public TranscriptSegment Segment { get; }
    public string Header { get; }
    public string Text { get; }
    public AnonymousVisualEvidenceViewModel VisualEvidence => _visualEvidence;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetVisualEvidence(AnonymousVisualEvidenceViewModel visualEvidence)
    {
        ArgumentNullException.ThrowIfNull(visualEvidence);
        if (ReferenceEquals(_visualEvidence, visualEvidence) || _visualEvidence == visualEvidence) return;
        _visualEvidence = visualEvidence;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VisualEvidence)));
    }
}
