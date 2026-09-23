using Trazio.AsistenteReunion.App;

namespace Trazio.AsistenteReunion.Tests;

public sealed class BoundedDropOldestProcessorTests
{
    [Fact]
    [Trait("Area", "VisualCapture")]
    public async Task CapacityTwo_WhenThirdItemArrives_DropsOldestAndDisposesEveryItemOnce()
    {
        await using var processor = new BoundedDropOldestProcessor<TrackedItem>();
        var first = new TrackedItem(1);
        var second = new TrackedItem(2);
        var third = new TrackedItem(3);
        var processed = new List<int>();

        Assert.True(processor.TryWrite(first));
        Assert.True(processor.TryWrite(second));
        Assert.True(processor.TryWrite(third));
        processor.Complete();
        await processor.RunAsync((item, _) =>
        {
            processed.Add(item.Id);
            return ValueTask.CompletedTask;
        });

        Assert.Equal([2, 3], processed);
        Assert.Equal(1, processor.DroppedCount);
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, second.DisposeCount);
        Assert.Equal(1, third.DisposeCount);
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public async Task RunAsync_WhenCancelled_DisposesConsumedAndBufferedItemsExactlyOnce()
    {
        await using var processor = new BoundedDropOldestProcessor<TrackedItem>();
        var consumed = new TrackedItem(1);
        var buffered = new TrackedItem(2);
        using var cancellation = new CancellationTokenSource();
        var consuming = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        processor.TryWrite(consumed);
        processor.TryWrite(buffered);

        var run = processor.RunAsync(async (_, token) =>
        {
            consuming.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }, cancellation.Token);
        await consuming.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(1, consumed.DisposeCount);
        Assert.Equal(1, buffered.DisposeCount);
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public async Task TryWrite_AfterCompletion_RejectsAndDisposesIncomingItemOnce()
    {
        await using var processor = new BoundedDropOldestProcessor<TrackedItem>();
        var item = new TrackedItem(1);
        processor.Complete();

        var accepted = processor.TryWrite(item);

        Assert.False(accepted);
        Assert.Equal(1, item.DisposeCount);
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public async Task DisposeAsync_WithBlockedConsumer_CancelsAndWaitsUntilNoItemIsRetained()
    {
        var processor = new BoundedDropOldestProcessor<TrackedItem>();
        var consumed = new TrackedItem(1);
        var buffered = new TrackedItem(2);
        var consumerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseConsumer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        processor.TryWrite(consumed);
        processor.TryWrite(buffered);
        var run = processor.RunAsync(async (_, _) =>
        {
            consumerStarted.TrySetResult();
            await releaseConsumer.Task;
        });
        await consumerStarted.Task;

        var disposing = processor.DisposeAsync().AsTask();

        Assert.False(disposing.IsCompleted);
        Assert.Equal(0, consumed.DisposeCount);
        releaseConsumer.SetResult();
        await disposing;
        try
        {
            await run;
        }
        catch (OperationCanceledException)
        {
            // Completion and cancellation may race after the active callback is released.
        }
        Assert.Equal(1, consumed.DisposeCount);
        Assert.Equal(1, buffered.DisposeCount);
    }

    private sealed class TrackedItem(int id) : IDisposable
    {
        public int Id { get; } = id;
        public int DisposeCount { get; private set; }
        public void Dispose() => DisposeCount++;
    }
}
