using NAudio.Wave;
using System.IO;
using System.Security.Cryptography;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.App;

public sealed class AudioPlaybackService : IAsyncDisposable
{
    private CancellationTokenSource? _cancellation;
    private WaveOutEvent? _output;
    private readonly object _sync = new();
    private long _elapsedTicks;

    public TimeSpan Elapsed => TimeSpan.FromTicks(Interlocked.Read(ref _elapsedTicks));

    public Task PlayAsync(AudioArchiveStore archive, IReadOnlyList<ArchivedAudioChunk> chunks, CancellationToken cancellationToken = default) =>
        PlayFromAsync(archive, chunks, TimeSpan.Zero, null, cancellationToken);

    public async Task PlayFromAsync(
        AudioArchiveStore archive,
        IReadOnlyList<ArchivedAudioChunk> chunks,
        TimeSpan offsetIntoFirstChunk,
        TimeSpan? maximumDuration,
        CancellationToken cancellationToken = default)
    {
        Stop();
        Interlocked.Exchange(ref _elapsedTicks, 0);
        var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_sync) _cancellation = runCancellation;
        var budget = maximumDuration is { } duration ? new PlaybackDurationBudget(duration) : null;
        var completed = TimeSpan.Zero;
        try
        {
            for (var index = 0; index < chunks.Count && budget?.IsExhausted != true; index++)
            {
                runCancellation.Token.ThrowIfCancellationRequested();
                var wav = await archive.ReadChunkAsync(chunks[index], runCancellation.Token);
                try
                {
                    using var memory = new MemoryStream(wav, writable: false);
                    using var reader = new WaveFileReader(memory);
                    if (index == 0 && offsetIntoFirstChunk > TimeSpan.Zero)
                        reader.CurrentTime = offsetIntoFirstChunk < reader.TotalTime ? offsetIntoFirstChunk : reader.TotalTime;
                    var chunkStart = reader.CurrentTime;
                    budget?.StartChunk(chunkStart);
                    using var output = new WaveOutEvent();
                    lock (_sync) if (ReferenceEquals(_cancellation, runCancellation)) _output = output;
                    var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    output.PlaybackStopped += (_, e) =>
                    {
                        if (e.Exception is not null) stopped.TrySetException(e.Exception);
                        else stopped.TrySetResult();
                    };
                    using var registration = runCancellation.Token.Register(output.Stop);
                    output.Init(reader);
                    output.Play();
                    var monitor = MonitorProgressAsync(reader, output, budget, completed, chunkStart, runCancellation.Token);
                    try { await stopped.Task.WaitAsync(runCancellation.Token); }
                    finally
                    {
                        output.Stop();
                        try { await monitor; } catch (OperationCanceledException) when (runCancellation.IsCancellationRequested) { }
                    }
                    var played = reader.CurrentTime - chunkStart;
                    if (played > TimeSpan.Zero) completed += played;
                    Interlocked.Exchange(ref _elapsedTicks, completed.Ticks);
                }
                finally { CryptographicOperations.ZeroMemory(wav); }
            }
        }
        finally
        {
            runCancellation.Cancel();
            lock (_sync)
            {
                if (ReferenceEquals(_cancellation, runCancellation)) { _output = null; _cancellation = null; }
            }
            runCancellation.Dispose();
        }
    }

    private async Task MonitorProgressAsync(
        WaveFileReader reader,
        WaveOutEvent output,
        PlaybackDurationBudget? budget,
        TimeSpan completed,
        TimeSpan chunkStart,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        while (output.PlaybackState != PlaybackState.Stopped && await timer.WaitForNextTickAsync(cancellationToken))
        {
            var played = reader.CurrentTime - chunkStart;
            Interlocked.Exchange(ref _elapsedTicks, (completed + (played > TimeSpan.Zero ? played : TimeSpan.Zero)).Ticks);
            if (budget is null) continue;
            budget.Observe(reader.CurrentTime);
            if (!budget.IsExhausted) continue;
            output.Stop();
            break;
        }
        budget?.Observe(reader.CurrentTime);
    }

    public void Pause()
    {
        lock (_sync) _output?.Pause();
    }

    public void Resume()
    {
        lock (_sync) _output?.Play();
    }

    public void Stop()
    {
        CancellationTokenSource? cancellation;
        WaveOutEvent? output;
        lock (_sync) { cancellation = _cancellation; output = _output; }
        cancellation?.Cancel();
        output?.Stop();
    }

    public ValueTask DisposeAsync() { Stop(); return ValueTask.CompletedTask; }
}