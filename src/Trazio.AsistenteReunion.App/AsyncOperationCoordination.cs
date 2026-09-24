using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.App;

public sealed record HistoryLoadTicket(long Generation, string SessionId, CancellationToken CancellationToken);

public sealed record HistorySearchTicket(long Generation, string Query, CancellationToken CancellationToken);

public sealed record HistorySearchNavigationKey(
    string SessionId,
    string? SegmentId,
    AudioSourceKind? Source);

public sealed record HistorySearchNavigationTicket(
    long Generation,
    HistorySearchNavigationKey Key,
    CancellationToken CancellationToken);

public sealed class HistorySearchCoordinator : IDisposable
{
    private long _generation;
    private CancellationTokenSource? _cancellation;
    private HistorySearchTicket? _current;

    public HistorySearchTicket Begin(string query)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var cancellation = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _cancellation, cancellation);
        previous?.Cancel();
        previous?.Dispose();
        var ticket = new HistorySearchTicket(Interlocked.Increment(ref _generation), query, cancellation.Token);
        _current = ticket;
        return ticket;
    }

    public bool IsCurrent(HistorySearchTicket ticket, string? visibleQuery) =>
        _current?.Generation == ticket.Generation &&
        !ticket.CancellationToken.IsCancellationRequested &&
        string.Equals(ticket.Query, visibleQuery?.Trim(), StringComparison.Ordinal);

    public void Invalidate()
    {
        Interlocked.Increment(ref _generation);
        _current = null;
        var cancellation = Interlocked.Exchange(ref _cancellation, null);
        cancellation?.Cancel();
        cancellation?.Dispose();
    }

    public void Dispose() => Invalidate();
}

public sealed class HistorySearchNavigationCoordinator : IDisposable
{
    private long _generation;
    private CancellationTokenSource? _cancellation;
    private HistorySearchNavigationTicket? _current;

    public HistorySearchNavigationTicket Begin(HistorySearchNavigationKey key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key.SessionId);
        var cancellation = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _cancellation, cancellation);
        previous?.Cancel();
        previous?.Dispose();
        var ticket = new HistorySearchNavigationTicket(
            Interlocked.Increment(ref _generation),
            key,
            cancellation.Token);
        _current = ticket;
        return ticket;
    }

    public bool IsCurrent(HistorySearchNavigationTicket ticket, HistorySearchNavigationKey? selectedKey) =>
        _current?.Generation == ticket.Generation &&
        !ticket.CancellationToken.IsCancellationRequested &&
        ticket.Key == selectedKey;

    public bool IsCurrent(HistorySearchNavigationTicket ticket) =>
        _current?.Generation == ticket.Generation &&
        !ticket.CancellationToken.IsCancellationRequested;

    public void Invalidate()
    {
        Interlocked.Increment(ref _generation);
        _current = null;
        var cancellation = Interlocked.Exchange(ref _cancellation, null);
        cancellation?.Cancel();
        cancellation?.Dispose();
    }

    public void Dispose() => Invalidate();
}

public sealed class HistorySearchActivityGate
{
    private readonly object _gate = new();
    private int _activeOperations;
    private bool _accepting = true;
    private TaskCompletionSource? _drained;

    public bool IsAccepting
    {
        get { lock (_gate) return _accepting; }
    }

    public bool TryBegin(out Lease lease)
    {
        lock (_gate)
        {
            if (!_accepting)
            {
                lease = null!;
                return false;
            }

            if (_activeOperations == 0)
                _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _activeOperations++;
            lease = new Lease(this);
            return true;
        }
    }

    public Task BlockAndDrainAsync()
    {
        lock (_gate)
        {
            _accepting = false;
            return _activeOperations == 0 ? Task.CompletedTask : _drained!.Task;
        }
    }

    public void Reopen()
    {
        lock (_gate) _accepting = true;
    }

    private void Complete()
    {
        TaskCompletionSource? drained = null;
        lock (_gate)
        {
            if (_activeOperations <= 0) return;
            _activeOperations--;
            if (_activeOperations == 0)
            {
                drained = _drained;
                _drained = null;
            }
        }
        drained?.TrySetResult();
    }

    public sealed class Lease : IDisposable
    {
        private HistorySearchActivityGate? _owner;

        internal Lease(HistorySearchActivityGate owner) => _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Complete();
    }
}

public sealed class HistorySelectionCoordinator : IDisposable
{
    private long _generation;
    private CancellationTokenSource? _cancellation;
    private HistoryLoadTicket? _current;
    private readonly List<CancellationTokenSource> _retired = [];

    public HistoryLoadTicket Begin(string sessionId, CancellationToken parentCancellation = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var cancellation = parentCancellation.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(parentCancellation)
            : new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _cancellation, cancellation);
        previous?.Cancel();
        previous?.Dispose();
        var ticket = new HistoryLoadTicket(Interlocked.Increment(ref _generation), sessionId, cancellation.Token);
        _current = ticket;
        return ticket;
    }

    public HistoryLoadTicket Capture(string sessionId)
    {
        var current = _current;
        if (current is null || !string.Equals(current.SessionId, sessionId, StringComparison.Ordinal) || current.CancellationToken.IsCancellationRequested)
            throw new OperationCanceledException("Cambió la sesión seleccionada del historial.");
        return current;
    }

    public bool IsCurrent(HistoryLoadTicket ticket, string? selectedSessionId) =>
        _current?.Generation == ticket.Generation &&
        !ticket.CancellationToken.IsCancellationRequested &&
        string.Equals(ticket.SessionId, selectedSessionId, StringComparison.Ordinal);

    public void Invalidate()
    {
        Interlocked.Increment(ref _generation);
        _current = null;
        var cancellation = Interlocked.Exchange(ref _cancellation, null);
        cancellation?.Cancel();
        cancellation?.Dispose();
    }

    public void Dispose()
    {
        Invalidate();
        foreach (var cancellation in _retired) cancellation.Dispose();
        _retired.Clear();
    }
}

public sealed class PlaybackOperationCoordinator
{
    private long _generation;
    public long Begin() => Interlocked.Increment(ref _generation);
    public void Supersede() => Interlocked.Increment(ref _generation);
    public bool IsCurrent(long generation) => Volatile.Read(ref _generation) == generation;
}

public sealed record RevisionSelectionTicket(
    long Generation,
    string SessionId,
    AudioSourceKind Source,
    string? RevisionId,
    CancellationToken CancellationToken);

public sealed class RevisionSelectionCoordinator : IDisposable
{
    private long _generation;
    private CancellationTokenSource? _cancellation;
    private RevisionSelectionTicket? _current;

    public RevisionSelectionTicket Begin(string sessionId, AudioSourceKind source, string? revisionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var cancellation = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _cancellation, cancellation);
        previous?.Cancel();
        previous?.Dispose();
        var ticket = new RevisionSelectionTicket(
            Interlocked.Increment(ref _generation), sessionId, source, revisionId, cancellation.Token);
        _current = ticket;
        return ticket;
    }

    public bool IsCurrent(
        RevisionSelectionTicket ticket,
        string? selectedSessionId,
        AudioSourceKind selectedSource,
        string? selectedRevisionId) =>
        _current?.Generation == ticket.Generation &&
        !ticket.CancellationToken.IsCancellationRequested &&
        string.Equals(ticket.SessionId, selectedSessionId, StringComparison.Ordinal) &&
        ticket.Source == selectedSource &&
        string.Equals(ticket.RevisionId, selectedRevisionId, StringComparison.Ordinal);

    public void Invalidate()
    {
        Interlocked.Increment(ref _generation);
        _current = null;
        var cancellation = Interlocked.Exchange(ref _cancellation, null);
        cancellation?.Cancel();
        cancellation?.Dispose();
    }

    public void Dispose() => Invalidate();
}
public sealed class PlaybackDurationBudget(TimeSpan maximumDuration)
{
    private TimeSpan? _lastPosition;
    public TimeSpan Remaining { get; private set; } = maximumDuration;
    public bool IsExhausted => Remaining <= TimeSpan.Zero;

    public void StartChunk(TimeSpan position) => _lastPosition = position;

    public void Observe(TimeSpan position)
    {
        if (_lastPosition is null) { _lastPosition = position; return; }
        var elapsedAudio = position - _lastPosition.Value;
        if (elapsedAudio > TimeSpan.Zero) Remaining -= elapsedAudio;
        _lastPosition = position;
    }
}
public sealed class OwnedCancellationOperationCoordinator : IDisposable
{
    private readonly object _gate = new();
    private Operation? _current;
    private long _generation;

    public bool IsRunning { get { lock (_gate) return _current is not null; } }

    public bool TryBegin(IEnumerable<CancellationToken> parentTokens, out Operation operation)
    {
        lock (_gate)
        {
            if (_current is not null) { operation = null!; return false; }
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(parentTokens.ToArray());
            operation = new(++_generation, cancellation);
            _current = operation;
            return true;
        }
    }

    public bool IsCurrent(Operation operation) { lock (_gate) return ReferenceEquals(_current, operation); }

    public bool Complete(Operation operation)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_current, operation)) return false;
            _current = null;
        }
        operation.Complete();
        return true;
    }

    public void Cancel()
    {
        Operation? operation;
        lock (_gate) operation = _current;
        operation?.Cancel();
    }

    public async Task CancelAndWaitAsync()
    {
        Operation? operation;
        lock (_gate) operation = _current;
        if (operation is null) return;
        operation.Cancel();
        await operation.Completion;
    }

    public void Dispose() => Cancel();

    public sealed class Operation(long generation, CancellationTokenSource cancellation)
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _finished;
        public long Generation { get; } = generation;
        public CancellationToken CancellationToken => cancellation.Token;
        public Task Completion => _completion.Task;
        internal void Cancel() { try { cancellation.Cancel(); } catch (ObjectDisposedException) { } }
        internal void Complete()
        {
            if (Interlocked.Exchange(ref _finished, 1) != 0) return;
            cancellation.Dispose();
            _completion.TrySetResult();
        }
    }
}
