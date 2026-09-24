using System.ComponentModel;
using System.Runtime.CompilerServices;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.App;

public sealed record PendingReviewItem(
    PendingSegmentReview Pending,
    string MeetingTitle,
    string Details,
    string Snippet,
    string StateLabel,
    string AutomationName) : INotifyPropertyChanged
{
    private bool _isBatchSelected;

    public bool IsBatchSelected
    {
        get => _isBatchSelected;
        set
        {
            if (_isBatchSelected == value) return;
            _isBatchSelected = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public static PendingReviewItem From(
        PendingSegmentReview pending,
        TimeZoneInfo? timeZone = null)
    {
        ArgumentNullException.ThrowIfNull(pending);
        timeZone ??= TimeZoneInfo.Local;
        var localStart = TimeZoneInfo.ConvertTime(pending.SessionStartedAt, timeZone);
        var source = pending.Source == AudioSourceKind.Microphone
            ? string.IsNullOrWhiteSpace(pending.SpeakerName) ? "Micrófono" : pending.SpeakerName
            : "Audio del equipo";
        var time = pending.Start.TotalHours >= 1
            ? pending.Start.ToString("hh\\:mm\\:ss")
            : pending.Start.ToString("mm\\:ss");
        var details = $"{localStart:yyyy-MM-dd HH:mm} · {source} · {time}";
        const string state = "Pendiente de revisión";
        var snippet = HistorySearchText.CreateSnippet(pending.Text, maximumLength: 140);
        return new(
            pending,
            pending.SessionTitle,
            details,
            snippet,
            state,
            $"{pending.SessionTitle}. {details}. {state}. {pending.Text}");
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed record PendingReviewBatchSelectionState(
    int SelectedCount,
    bool CanApprove,
    string ButtonLabel,
    IReadOnlyList<SegmentReviewApprovalRequest> Requests);

public static class PendingReviewBatchSelectionPresenter
{
    public const string DefaultButtonLabel = "Marcar seleccionados como revisados";

    public static PendingReviewBatchSelectionState Create(
        IEnumerable<PendingReviewItem> items,
        bool canInteract)
    {
        ArgumentNullException.ThrowIfNull(items);
        var selected = items.Where(item => item.IsBatchSelected).ToArray();
        var requests = selected
            .Select(item => new SegmentReviewApprovalRequest(
                item.Pending.SessionId,
                item.Pending.SegmentId,
                item.Pending.ExpectedDecisionRevision))
            .ToArray();
        var label = requests.Length switch
        {
            0 => DefaultButtonLabel,
            1 => "Marcar 1 original como revisado",
            _ => $"Marcar {requests.Length} originales como revisados"
        };
        return new(
            requests.Length,
            canInteract && requests.Length > 0,
            label,
            requests);
    }
}

public sealed record PendingReviewPresentationState(
    IReadOnlyList<PendingReviewItem> Items,
    string Status,
    bool IsTruncated);

public static class PendingReviewPresenter
{
    public const string LoadingStatus = "Cargando segmentos pendientes…";
    public const string ErrorStatus = "No se pudo cargar la revisión. No se modificó ningún segmento.";
    public const string CompleteStatus = "Revisión completa: no quedan segmentos pendientes.";

    public static PendingReviewPresentationState Create(
        PendingSegmentReviewResult result,
        TimeZoneInfo? timeZone = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        var items = result.Items.Select(item => PendingReviewItem.From(item, timeZone)).ToArray();
        var status = result.IsTruncated
            ? $"Se muestran los primeros {items.Length} de {result.TotalCount} segmentos pendientes."
            : result.TotalCount == 0
                ? CompleteStatus
                : result.TotalCount == 1
                    ? "Hay 1 segmento pendiente de revisión."
                    : $"Hay {result.TotalCount} segmentos pendientes de revisión.";
        return new(items, status, result.IsTruncated);
    }
}

public sealed record PendingReviewNavigationIntent(
    string SessionId,
    string SegmentId,
    AudioSourceKind Source,
    bool UseOriginalRevision,
    bool AutoPlay)
{
    public static PendingReviewNavigationIntent From(PendingReviewItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new(
            item.Pending.SessionId,
            item.Pending.SegmentId,
            item.Pending.Source,
            UseOriginalRevision: true,
            AutoPlay: false);
    }
}

public sealed record SegmentReviewActionsState(
    bool CanMarkReviewed,
    bool CanReopen,
    bool IsOriginalReviewEligible);

public static class SegmentReviewActionsPresenter
{
    public static bool IsSessionEligible(SessionState? state) =>
        state is SessionState.Completed or SessionState.Interrupted;

    public static SegmentReviewActionsState Create(
        ReviewedTranscriptSegment? review,
        SessionState? sessionState,
        bool viewingModelRevision,
        bool hasUnsavedDraft)
    {
        var eligible = review is not null &&
            IsSessionEligible(sessionState) &&
            !viewingModelRevision &&
            !review.HasCorrectionHistory;
        if (!eligible) return new(false, false, false);
        if (hasUnsavedDraft) return new(false, false, true);
        return review!.IsOriginalApproved
            ? new(false, true, true)
            : new(true, false, true);
    }
}

public sealed record CorrectionDraftSnapshot(
    string SessionId,
    string SegmentId,
    AudioSourceKind Source,
    string SavedText,
    string EditorText);

public sealed record CorrectionDraftTransition(
    bool CanNavigate,
    string SessionId,
    string SegmentId,
    AudioSourceKind Source,
    string EditorText);

public sealed record CorrectionEditorContext(
    string SessionId,
    string SegmentId,
    AudioSourceKind Source);

public readonly record struct CorrectionEditorOperationTicket(
    long Revision,
    CorrectionEditorContext? Context);

public sealed record CorrectionEditorSaveTicket(
    CorrectionEditorOperationTicket Operation,
    string FrozenText);

public sealed record CorrectionEditorOperationResult<T>(
    T Value,
    bool CanReplaceEditor);

public sealed record CorrectionEditorSaveOperationResult(
    string FrozenText,
    CorrectionEditorBaselineAdvance Baseline);

public readonly record struct CorrectionEditorBaselineAdvance(
    bool CanReplaceEditor,
    bool HasUnsavedDraft);

public sealed class CorrectionDraftNavigationGuard
{
    public const string BlockedStatus =
        "Hay cambios sin guardar en el texto corregido. Usa “Guardar corrección” o “Descartar borrador” antes de cambiar de sesión, segmento, pista o versión.";

    private readonly object _gate = new();
    private CorrectionEditorContext? _context;
    private string _savedText = string.Empty;
    private string _editorText = string.Empty;
    private long _revision;

    public CorrectionDraftSnapshot? Current
    {
        get
        {
            lock (_gate) return CurrentUnsafe();
        }
    }

    public bool HasUnsavedDraft
    {
        get
        {
            lock (_gate) return CurrentUnsafe() is not null;
        }
    }

    public void SetContext(
        string sessionId,
        string segmentId,
        AudioSourceKind source,
        string savedText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(segmentId);
        ArgumentNullException.ThrowIfNull(savedText);
        lock (_gate)
        {
            _context = new(sessionId, segmentId, source);
            _savedText = savedText;
            _editorText = savedText;
            _revision++;
        }
    }

    public void ObserveEditor(string editorText)
    {
        ArgumentNullException.ThrowIfNull(editorText);
        lock (_gate)
        {
            if (_context is null || string.Equals(_editorText, editorText, StringComparison.Ordinal)) return;
            _editorText = editorText;
            _revision++;
        }
    }

    public CorrectionEditorOperationTicket CaptureOperation()
    {
        lock (_gate) return new(_revision, _context);
    }

    public CorrectionEditorSaveTicket CaptureSave()
    {
        lock (_gate)
        {
            if (_context is null)
                throw new InvalidOperationException("No hay un segmento de corrección seleccionado.");
            return new(new(_revision, _context), _editorText);
        }
    }

    public bool CanReplaceEditor(CorrectionEditorOperationTicket ticket)
    {
        lock (_gate)
            return ticket.Revision == _revision && Equals(ticket.Context, _context);
    }

    public async Task<CorrectionEditorOperationResult<T>> RunAsync<T>(
        CorrectionEditorOperationTicket ticket,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var value = await operation(cancellationToken);
        return new(value, CanReplaceEditor(ticket));
    }

    public async Task<bool> RunAsync(
        CorrectionEditorOperationTicket ticket,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await operation(cancellationToken);
        return CanReplaceEditor(ticket);
    }

    public async Task<CorrectionEditorSaveOperationResult> RunSaveAsync(
        CorrectionEditorSaveTicket ticket,
        Func<string, CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await operation(ticket.FrozenText, cancellationToken);
        return new(
            ticket.FrozenText,
            AdvanceSavedText(ticket.Operation, ticket.FrozenText));
    }

    public CorrectionEditorBaselineAdvance AdvanceSavedText(
        CorrectionEditorOperationTicket ticket,
        string savedText)
    {
        ArgumentNullException.ThrowIfNull(savedText);
        lock (_gate)
        {
            if (_context is null || !Equals(ticket.Context, _context))
                return new(false, CurrentUnsafe() is not null);

            var canReplaceEditor = ticket.Revision == _revision;
            _savedText = savedText;
            if (canReplaceEditor) _editorText = savedText;
            _revision++;
            return new(canReplaceEditor, CurrentUnsafe() is not null);
        }
    }

    public string? DiscardDraft()
    {
        lock (_gate)
        {
            if (_context is null) return null;
            _editorText = _savedText;
            _revision++;
            return _savedText;
        }
    }

    public CorrectionDraftTransition ResolveSegmentTransition(
        string requestedSessionId,
        string requestedSegmentId,
        AudioSourceKind requestedSource,
        string requestedEditorText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedSessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedSegmentId);
        var draft = Current;
        return draft is null
            ? new(true, requestedSessionId, requestedSegmentId, requestedSource, requestedEditorText)
            : new(false, draft.SessionId, draft.SegmentId, draft.Source, draft.EditorText);
    }

    public bool CanNavigate() => !HasUnsavedDraft;

    public void Clear()
    {
        lock (_gate)
        {
            _context = null;
            _savedText = string.Empty;
            _editorText = string.Empty;
            _revision++;
        }
    }

    private CorrectionDraftSnapshot? CurrentUnsafe() =>
        _context is not null && !string.Equals(_savedText, _editorText, StringComparison.Ordinal)
            ? new(
                _context.SessionId,
                _context.SegmentId,
                _context.Source,
                _savedText,
                _editorText)
            : null;
}

public static class PendingReviewQueryDispatcher
{
    public static Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Task.Run(() => query(cancellationToken), cancellationToken);
    }
}

public sealed record PendingReviewTicket(long Generation, CancellationToken CancellationToken);

public sealed class PendingReviewCoordinator : IDisposable
{
    private long _generation;
    private CancellationTokenSource? _cancellation;
    private PendingReviewTicket? _current;

    public PendingReviewTicket Begin()
    {
        Invalidate();
        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        var ticket = new PendingReviewTicket(
            Interlocked.Increment(ref _generation),
            cancellation.Token);
        _current = ticket;
        return ticket;
    }

    public bool IsCurrent(PendingReviewTicket ticket) =>
        ReferenceEquals(_current, ticket) && !ticket.CancellationToken.IsCancellationRequested;

    public void Invalidate()
    {
        Interlocked.Increment(ref _generation);
        _current = null;
        var cancellation = Interlocked.Exchange(ref _cancellation, null);
        if (cancellation is null) return;
        try { cancellation.Cancel(); }
        finally { cancellation.Dispose(); }
    }

    public void Dispose() => Invalidate();
}
