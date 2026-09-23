using System.Threading.Channels;

namespace Trazio.AsistenteReunion.App;

public sealed class BoundedDropOldestProcessor<T> : IFrameSampler<T> where T : IDisposable
{
    private const int Capacity = 2;
    private readonly Channel<T> _channel;
    private readonly object _lifecycleGate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private TaskCompletionSource? _runCompletion;
    private Task? _disposeTask;
    private bool _runStarted;
    private int _completed;
    private long _droppedCount;

    public BoundedDropOldestProcessor()
    {
        _channel = Channel.CreateBounded<T>(new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        }, item =>
        {
            Interlocked.Increment(ref _droppedCount);
            DisposeSafely(item);
        });
    }

    public long DroppedCount => Interlocked.Read(ref _droppedCount);

    public bool TryWrite(T item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (Volatile.Read(ref _completed) == 0 && _channel.Writer.TryWrite(item)) return true;

        DisposeSafely(item);
        return false;
    }

    public async Task RunAsync(
        Func<T, CancellationToken, ValueTask> process,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(process);

        TaskCompletionSource runCompletion;
        lock (_lifecycleGate)
        {
            if (_disposeTask is not null) throw new ObjectDisposedException(nameof(BoundedDropOldestProcessor<T>));
            if (_runStarted) throw new InvalidOperationException("The frame processor already has a consumer.");
            _runStarted = true;
            runCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _runCompletion = runCompletion;
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _shutdown.Token);

        try
        {
            await foreach (var item in _channel.Reader.ReadAllAsync(linkedCancellation.Token))
            {
                try
                {
                    await process(item, linkedCancellation.Token);
                }
                finally
                {
                    DisposeSafely(item);
                }
            }
        }
        finally
        {
            Complete();
            Drain();
            runCompletion.TrySetResult();
        }
    }

    public void Complete()
    {
        if (Interlocked.Exchange(ref _completed, 1) == 0) _channel.Writer.TryComplete();
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource? disposeCompletion = null;
        Task runCompletion;
        Task disposeTask;
        lock (_lifecycleGate)
        {
            if (_disposeTask is not null) return new ValueTask(_disposeTask);

            disposeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposeTask = disposeCompletion.Task;
            disposeTask = _disposeTask;
            runCompletion = _runCompletion?.Task ?? Task.CompletedTask;
        }

        _ = DisposeCoreAsync(runCompletion, disposeCompletion);
        return new ValueTask(disposeTask);
    }

    private async Task DisposeCoreAsync(Task runCompletion, TaskCompletionSource disposeCompletion)
    {
        try
        {
            Complete();
            try
            {
                await _shutdown.CancelAsync();
            }
            catch
            {
                // Consumer cancellation failures remain isolated to the visual pipeline.
            }

            await runCompletion;
            Drain();
        }
        finally
        {
            _shutdown.Dispose();
            disposeCompletion.TrySetResult();
        }
    }

    private void Drain()
    {
        while (_channel.Reader.TryRead(out var item)) DisposeSafely(item);
    }

    private static void DisposeSafely(T item)
    {
        try
        {
            item.Dispose();
        }
        catch
        {
            // Cleanup failures must not escape into audio or transcription flows.
        }
    }
}
