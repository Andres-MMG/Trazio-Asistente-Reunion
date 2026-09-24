using NAudio.Wave;
using System.IO;
using System.Security.Cryptography;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.App;

public sealed class AudioPlaybackService : IAsyncDisposable
{
    private readonly IPlaybackOutputFactory _outputFactory;
    private readonly SemaphoreSlim _transition = new(1, 1);
    private readonly object _sync = new();
    private PlaybackRun? _activeRun;
    private Task _activeTask = Task.CompletedTask;
    private IPlaybackOutput? _output;
    private PlaybackOutputClock? _outputClock;
    private TimeSpan _completedBeforeOutput;
    private long _elapsedTicks;
    private long _nextGeneration;
    private bool _pauseRequested;
    private bool _disposed;

    public AudioPlaybackService() : this(new WaveOutPlaybackOutputFactory()) { }

    internal AudioPlaybackService(IPlaybackOutputFactory outputFactory) =>
        _outputFactory = outputFactory ?? throw new ArgumentNullException(nameof(outputFactory));

    public TimeSpan Elapsed
    {
        get
        {
            lock (_sync)
            {
                if (_activeRun is not null && _output is not null && _outputClock is not null)
                    PublishElapsed(_activeRun, _completedBeforeOutput + _outputClock.Observe(SafeGetPosition(_output)));
                return TimeSpan.FromTicks(Interlocked.Read(ref _elapsedTicks));
            }
        }
    }

    public Task PlayAsync(
        AudioArchiveStore archive,
        IReadOnlyList<ArchivedAudioChunk> chunks,
        CancellationToken cancellationToken = default) =>
        PlayFromAsync(archive, chunks, TimeSpan.Zero, null, cancellationToken);

    public Task PlayPlanAsync(
        AudioArchiveStore archive,
        SegmentPlaybackPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(plan);
        return PlaySlicesAsync(
            archive.ReadChunkAsync,
            plan.Timeline.Spans
                .Select(span => new AudioPlaybackSlice(
                    span.Chunk,
                    span.OffsetIntoChunk,
                    span.SessionEnd - span.SessionStart))
                .ToArray(),
            cancellationToken);
    }

    public Task PlayFromAsync(
        AudioArchiveStore archive,
        IReadOnlyList<ArchivedAudioChunk> chunks,
        TimeSpan offsetIntoFirstChunk,
        TimeSpan? maximumDuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(chunks);
        var remaining = maximumDuration;
        var slices = new List<AudioPlaybackSlice>(chunks.Count);
        for (var index = 0; index < chunks.Count && remaining != TimeSpan.Zero; index++)
        {
            var offset = index == 0 && offsetIntoFirstChunk > TimeSpan.Zero
                ? offsetIntoFirstChunk
                : TimeSpan.Zero;
            var available = chunks[index].Duration - offset;
            if (available <= TimeSpan.Zero) continue;
            var duration = remaining is { } budget && budget < available ? budget : available;
            if (duration <= TimeSpan.Zero) break;
            slices.Add(new(chunks[index], offset, duration));
            if (remaining is not null) remaining -= duration;
        }
        return PlaySlicesAsync(archive.ReadChunkAsync, slices, cancellationToken);
    }

    internal async Task PlaySlicesAsync(
        Func<ArchivedAudioChunk, CancellationToken, Task<byte[]>> readChunkAsync,
        IReadOnlyList<AudioPlaybackSlice> slices,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(readChunkAsync);
        ArgumentNullException.ThrowIfNull(slices);

        PlaybackRun run;
        Task playbackTask;
        await _transition.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await StopActiveRunCoreAsync().ConfigureAwait(false);
            run = new(
                Interlocked.Increment(ref _nextGeneration),
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken));
            lock (_sync)
            {
                _activeRun = run;
                _activeTask = Task.CompletedTask;
                _output = null;
                _outputClock = null;
                _completedBeforeOutput = TimeSpan.Zero;
                _pauseRequested = false;
                Interlocked.Exchange(ref _elapsedTicks, 0);
            }
            playbackTask = ExecutePlaybackAsync(run, readChunkAsync, slices);
            lock (_sync)
            {
                if (ReferenceEquals(_activeRun, run)) _activeTask = playbackTask;
            }
        }
        finally { _transition.Release(); }

        try { await playbackTask.ConfigureAwait(false); }
        finally { CompleteRun(run, playbackTask); }
    }

    private async Task ExecutePlaybackAsync(
        PlaybackRun run,
        Func<ArchivedAudioChunk, CancellationToken, Task<byte[]>> readChunkAsync,
        IReadOnlyList<AudioPlaybackSlice> slices)
    {
        using var cancellationRegistration = run.CancellationToken.Register(() => StopOutput(run));
        var completed = TimeSpan.Zero;
        foreach (var slice in slices)
        {
            run.CancellationToken.ThrowIfCancellationRequested();
            var wav = await readChunkAsync(slice.Chunk, run.CancellationToken).ConfigureAwait(false);
            try
            {
                run.CancellationToken.ThrowIfCancellationRequested();
                using var memory = new MemoryStream(wav, writable: false);
                using var reader = new WaveFileReader(memory);
                if (slice.OffsetIntoChunk > TimeSpan.Zero)
                    reader.CurrentTime = slice.OffsetIntoChunk < reader.TotalTime
                        ? slice.OffsetIntoChunk
                        : reader.TotalTime;
                var available = reader.TotalTime - reader.CurrentTime;
                var maximumDuration = slice.Duration < available ? slice.Duration : available;
                if (maximumDuration <= TimeSpan.Zero) continue;

                using var output = _outputFactory.Create();
                var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                output.PlaybackStopped += OnPlaybackStopped;
                try
                {
                    var playbackSource = new DurationLimitedWaveProvider(reader, maximumDuration);
                    output.Init(playbackSource);
                    var clock = new PlaybackOutputClock(
                        output.OutputWaveFormat,
                        SafeGetPosition(output),
                        playbackSource.MaximumDuration);
                    AttachOutput(run, output, clock, completed);
                    try
                    {
                        StartOutputIfRequested(run, output);
                        await stopped.Task.WaitAsync(run.CancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        var played = CaptureOutputElapsed(run, output, clock, completed);
                        try
                        {
                            if (output.PlaybackState != PlaybackState.Stopped) output.Stop();
                        }
                        catch (Exception) { }
                        finally { DetachOutput(run, output, completed + played); }
                    }

                    run.CancellationToken.ThrowIfCancellationRequested();
                    completed += clock.Elapsed;
                    PublishElapsed(run, completed);
                }
                finally { output.PlaybackStopped -= OnPlaybackStopped; }

                void OnPlaybackStopped(object? sender, StoppedEventArgs e)
                {
                    if (e.Exception is not null) stopped.TrySetException(e.Exception);
                    else stopped.TrySetResult();
                }
            }
            finally { CryptographicOperations.ZeroMemory(wav); }
        }
    }

    private void AttachOutput(
        PlaybackRun run,
        IPlaybackOutput output,
        PlaybackOutputClock clock,
        TimeSpan completed)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_activeRun, run) || run.CancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(run.CancellationToken);
            _output = output;
            _outputClock = clock;
            _completedBeforeOutput = completed;
            PublishElapsed(run, completed);
        }
    }

    private void StartOutputIfRequested(PlaybackRun run, IPlaybackOutput output)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_activeRun, run) || run.CancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(run.CancellationToken);
            if (!_pauseRequested) output.Play();
        }
    }

    private TimeSpan CaptureOutputElapsed(
        PlaybackRun run,
        IPlaybackOutput output,
        PlaybackOutputClock clock,
        TimeSpan completed)
    {
        lock (_sync)
        {
            var played = clock.Observe(SafeGetPosition(output));
            PublishElapsed(run, completed + played);
            return played;
        }
    }

    private void DetachOutput(PlaybackRun run, IPlaybackOutput output, TimeSpan elapsed)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_activeRun, run) || !ReferenceEquals(_output, output)) return;
            _output = null;
            _outputClock = null;
            _completedBeforeOutput = elapsed;
            Interlocked.Exchange(ref _elapsedTicks, elapsed.Ticks);
        }
    }

    private void StopOutput(PlaybackRun run)
    {
        IPlaybackOutput? output = null;
        try
        {
            lock (_sync)
            {
                if (!ReferenceEquals(_activeRun, run) || _output is null) return;
                if (_outputClock is not null)
                    PublishElapsed(run, _completedBeforeOutput + _outputClock.Observe(SafeGetPosition(_output)));
                output = _output;
            }
        }
        catch (Exception) { }
        try { output?.Stop(); }
        catch (Exception) { }
    }

    private void PublishElapsed(PlaybackRun run, TimeSpan elapsed)
    {
        if (!ReferenceEquals(_activeRun, run)) return;
        var bounded = elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
        var previous = TimeSpan.FromTicks(Interlocked.Read(ref _elapsedTicks));
        if (bounded < previous) bounded = previous;
        Interlocked.Exchange(ref _elapsedTicks, bounded.Ticks);
    }

    public PlaybackControlResult Pause()
    {
        lock (_sync)
        {
            _pauseRequested = true;
            try
            {
                _output?.Pause();
                return PlaybackControlResult.Success;
            }
            catch (Exception ex) { return PlaybackControlResult.Failed(ex.Message); }
        }
    }

    public PlaybackControlResult Resume()
    {
        lock (_sync)
        {
            _pauseRequested = false;
            try
            {
                _output?.Play();
                return PlaybackControlResult.Success;
            }
            catch (Exception ex) { return PlaybackControlResult.Failed(ex.Message); }
        }
    }

    public async Task StopAsync()
    {
        await _transition.WaitAsync().ConfigureAwait(false);
        try { await StopActiveRunCoreAsync().ConfigureAwait(false); }
        finally { _transition.Release(); }
    }

    private async Task StopActiveRunCoreAsync()
    {
        PlaybackRun? run;
        Task task;
        lock (_sync)
        {
            run = _activeRun;
            task = _activeTask;
        }
        if (run is null) return;

        try
        {
            run.Cancel();
            try { await task.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch { }
        }
        finally { CompleteRun(run, task); }
    }

    private void CompleteRun(PlaybackRun run, Task task)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_activeRun, run) && ReferenceEquals(_activeTask, task))
            {
                _activeRun = null;
                _activeTask = Task.CompletedTask;
                _output = null;
                _outputClock = null;
                _completedBeforeOutput = TimeSpan.FromTicks(Interlocked.Read(ref _elapsedTicks));
                _pauseRequested = false;
            }
        }
        run.Dispose();
    }

    private static long SafeGetPosition(IPlaybackOutput output)
    {
        try { return Math.Max(0, output.GetPosition()); }
        catch (Exception) { return 0; }
    }

    public async ValueTask DisposeAsync()
    {
        await _transition.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            await StopActiveRunCoreAsync().ConfigureAwait(false);
        }
        finally { _transition.Release(); }
    }

    private sealed class PlaybackRun(long generation, CancellationTokenSource cancellation) : IDisposable
    {
        private int _disposed;
        public long Generation { get; } = generation;
        public CancellationToken CancellationToken => cancellation.Token;
        public void Cancel()
        {
            try { cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) cancellation.Dispose();
        }
    }
}

internal interface IPlaybackOutput : IDisposable
{
    event EventHandler<StoppedEventArgs>? PlaybackStopped;
    PlaybackState PlaybackState { get; }
    WaveFormat OutputWaveFormat { get; }
    long GetPosition();
    void Init(IWaveProvider provider);
    void Play();
    void Pause();
    void Stop();
}

public readonly record struct PlaybackControlResult(bool Succeeded, string? ErrorMessage)
{
    public static PlaybackControlResult Success { get; } = new(true, null);
    public static PlaybackControlResult Failed(string? errorMessage) =>
        new(false, string.IsNullOrWhiteSpace(errorMessage) ? "El dispositivo de audio rechazó la operación." : errorMessage);
}

internal interface IPlaybackOutputFactory
{
    IPlaybackOutput Create();
}

internal sealed class WaveOutPlaybackOutputFactory : IPlaybackOutputFactory
{
    public IPlaybackOutput Create() => new WaveOutPlaybackOutput();
}

internal sealed class WaveOutPlaybackOutput : IPlaybackOutput
{
    private readonly WaveOutEvent _output = new();
    public event EventHandler<StoppedEventArgs>? PlaybackStopped
    {
        add => _output.PlaybackStopped += value;
        remove => _output.PlaybackStopped -= value;
    }
    public PlaybackState PlaybackState => _output.PlaybackState;
    public WaveFormat OutputWaveFormat => _output.OutputWaveFormat;
    public long GetPosition() => _output.GetPosition();
    public void Init(IWaveProvider provider) => _output.Init(provider);
    public void Play() => _output.Play();
    public void Pause() => _output.Pause();
    public void Stop() => _output.Stop();
    public void Dispose() => _output.Dispose();
}

internal sealed class PlaybackOutputClock
{
    private readonly WaveFormat _waveFormat;
    private readonly long _originBytes;
    private readonly TimeSpan _maximumDuration;
    private TimeSpan _elapsed;

    public PlaybackOutputClock(
        WaveFormat waveFormat,
        long originBytes,
        TimeSpan maximumDuration)
    {
        _waveFormat = waveFormat ?? throw new ArgumentNullException(nameof(waveFormat));
        _originBytes = Math.Max(0, originBytes);
        _maximumDuration = maximumDuration < TimeSpan.Zero ? TimeSpan.Zero : maximumDuration;
    }

    public TimeSpan Elapsed => _elapsed;

    public TimeSpan Observe(long devicePositionBytes)
    {
        var bytes = Math.Max(0, devicePositionBytes - _originBytes);
        bytes -= bytes % _waveFormat.BlockAlign;
        var observed = TimeSpan.FromSeconds(bytes / (double)_waveFormat.AverageBytesPerSecond);
        if (observed > _maximumDuration) observed = _maximumDuration;
        if (observed > _elapsed) _elapsed = observed;
        return _elapsed;
    }
}

internal sealed record AudioPlaybackSlice(
    ArchivedAudioChunk Chunk,
    TimeSpan OffsetIntoChunk,
    TimeSpan Duration);

internal sealed class DurationLimitedWaveProvider : IWaveProvider
{
    private readonly IWaveProvider _source;
    private long _remainingBytes;

    public DurationLimitedWaveProvider(IWaveProvider source, TimeSpan maximumDuration)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        if (maximumDuration <= TimeSpan.Zero)
        {
            _remainingBytes = 0;
            MaximumDuration = TimeSpan.Zero;
            return;
        }

        var requestedBytes = checked((long)Math.Floor(maximumDuration.TotalSeconds * WaveFormat.AverageBytesPerSecond));
        _remainingBytes = requestedBytes - requestedBytes % WaveFormat.BlockAlign;
        MaximumDuration = TimeSpan.FromSeconds(_remainingBytes / (double)WaveFormat.AverageBytesPerSecond);
    }

    public WaveFormat WaveFormat => _source.WaveFormat;
    public TimeSpan MaximumDuration { get; }

    public int Read(byte[] buffer, int offset, int count)
    {
        if (_remainingBytes <= 0) return 0;
        var boundedCount = (int)Math.Min(count, _remainingBytes);
        boundedCount -= boundedCount % WaveFormat.BlockAlign;
        if (boundedCount <= 0) return 0;
        var read = _source.Read(buffer, offset, boundedCount);
        _remainingBytes -= read;
        return read;
    }
}
