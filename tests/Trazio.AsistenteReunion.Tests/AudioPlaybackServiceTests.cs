using NAudio.Wave;
using System.Threading.Channels;
using Trazio.AsistenteReunion.App;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.Tests;

public sealed class AudioPlaybackServiceTests
{
    [Fact]
    public void DurationLimitedWaveProvider_StopsExactlyAtAlignedDurationBudget()
    {
        var source = new CountingWaveProvider(new WaveFormat(16_000, 16, 1), 32_000);
        var limited = new DurationLimitedWaveProvider(source, TimeSpan.FromMilliseconds(250));
        var buffer = new byte[4_096];

        var total = 0;
        int read;
        while ((read = limited.Read(buffer, 0, buffer.Length)) > 0) total += read;

        Assert.Equal(8_000, total);
        Assert.Equal(8_000, source.BytesRead);
        Assert.Equal(TimeSpan.FromMilliseconds(250), limited.MaximumDuration);
    }

    [Fact]
    public void PlaybackOutputClock_UsesDeviceBytesAndIgnoresReaderReadAhead()
    {
        var clock = new PlaybackOutputClock(
            new WaveFormat(16_000, 16, 1),
            originBytes: 1_000,
            maximumDuration: TimeSpan.FromSeconds(1));

        Assert.Equal(TimeSpan.Zero, clock.Observe(1_000));
        Assert.Equal(TimeSpan.FromMilliseconds(250), clock.Observe(9_000));
        Assert.Equal(TimeSpan.FromMilliseconds(250), clock.Observe(5_000));
        Assert.Equal(TimeSpan.FromSeconds(1), clock.Observe(90_000));
    }

    [Theory]
    [InlineData(75, 300)]
    [InlineData(100, 400)]
    [InlineData(125, 500)]
    [InlineData(150, 600)]
    [InlineData(200, 800)]
    public void PlaybackOutputClock_MapsOutputBytesToSourceElapsed(
        int percent,
        int expectedSourceMilliseconds)
    {
        var speed = PlaybackSpeed.FromPercent(percent);
        var clock = new PlaybackOutputClock(
            new WaveFormat(16_000, 16, 1),
            originBytes: 0,
            maximumDuration: TimeSpan.FromSeconds(1),
            sourceTimeScale: speed.Multiplier);

        var elapsed = clock.Observe(12_800);

        Assert.Equal(TimeSpan.FromMilliseconds(expectedSourceMilliseconds), elapsed);
    }

    [Fact]
    public async Task Elapsed_WhenProviderReadsAhead_TracksOnlyDevicePosition()
    {
        var factory = new FakePlaybackOutputFactory();
        await using var service = new AudioPlaybackService(factory);
        var wav = OneSecondWav();
        var slice = Slice("one", TimeSpan.FromSeconds(1));

        var playback = service.PlaySlicesAsync((_, _) => Task.FromResult(wav), [slice]);
        var output = await factory.NextAsync();
        await output.Played.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(32_000, output.ReadAheadBytes);
        Assert.Equal(TimeSpan.Zero, service.Elapsed);

        output.Advance(TimeSpan.FromMilliseconds(250));
        Assert.Equal(TimeSpan.FromMilliseconds(250), service.Elapsed);
        output.Complete();
        await playback.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(TimeSpan.FromMilliseconds(250), service.Elapsed);
        Assert.True(output.IsDisposed);
    }

    [Fact]
    public async Task StopAsync_WaitsForPreparationAndZerosReturnedPcm()
    {
        var factory = new FakePlaybackOutputFactory();
        await using var service = new AudioPlaybackService(factory);
        var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRead = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var wav = OneSecondWav();

        var playback = service.PlaySlicesAsync(
            async (_, _) =>
            {
                readStarted.TrySetResult();
                return await releaseRead.Task;
            },
            [Slice("preparing", TimeSpan.FromSeconds(1))]);
        await readStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var stopping = service.StopAsync();
        Assert.False(stopping.IsCompleted);
        releaseRead.TrySetResult(wav);

        await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => playback);
        Assert.All(wav, value => Assert.Equal((byte)0, value));
        Assert.Equal(0, factory.CreatedCount);
    }

    [Fact]
    public async Task NewPlaybackRun_CancelsAndWaitsForPredecessorCleanup()
    {
        var factory = new FakePlaybackOutputFactory();
        await using var service = new AudioPlaybackService(factory);
        var firstReadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstRead = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstWav = OneSecondWav();
        var secondWav = OneSecondWav();

        var first = service.PlaySlicesAsync(
            async (_, _) =>
            {
                firstReadStarted.TrySetResult();
                return await releaseFirstRead.Task;
            },
            [Slice("first", TimeSpan.FromSeconds(1))]);
        await firstReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var second = service.PlaySlicesAsync(
            (_, _) => Task.FromResult(secondWav),
            [Slice("second", TimeSpan.FromSeconds(1))]);
        Assert.False(second.IsCompleted);
        Assert.Equal(0, factory.CreatedCount);

        releaseFirstRead.TrySetResult(firstWav);
        var secondOutput = await factory.NextAsync();
        await secondOutput.Played.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.All(firstWav, value => Assert.Equal((byte)0, value));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal(TimeSpan.Zero, service.Elapsed);

        secondOutput.Advance(TimeSpan.FromSeconds(1));
        secondOutput.Complete();
        await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromSeconds(1), service.Elapsed);
    }

    [Fact]
    public async Task PauseDuringNextChunkPreparation_HoldsNewOutputUntilResume()
    {
        var factory = new FakePlaybackOutputFactory();
        await using var service = new AudioPlaybackService(factory);
        var secondReadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecondRead = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstWav = OneSecondWav();
        var secondWav = OneSecondWav();

        var playback = service.PlaySlicesAsync(
            async (chunk, _) =>
            {
                if (chunk.Id == "second")
                {
                    secondReadStarted.TrySetResult();
                    return await releaseSecondRead.Task;
                }
                return firstWav;
            },
            [Slice("first", TimeSpan.FromSeconds(1)), Slice("second", TimeSpan.FromSeconds(1))]);

        var firstOutput = await factory.NextAsync();
        await firstOutput.Played.WaitAsync(TimeSpan.FromSeconds(5));
        firstOutput.Advance(TimeSpan.FromSeconds(1));
        firstOutput.Complete();
        await secondReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        service.Pause();
        releaseSecondRead.TrySetResult(secondWav);
        var secondOutput = await factory.NextAsync();
        await secondOutput.Initialized.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, secondOutput.PlayCalls);
        Assert.Equal(TimeSpan.FromSeconds(1), service.Elapsed);

        service.Resume();
        await secondOutput.Played.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, secondOutput.PlayCalls);
        secondOutput.Advance(TimeSpan.FromSeconds(1));
        secondOutput.Complete();
        await playback.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(TimeSpan.FromSeconds(2), service.Elapsed);
    }

    [Fact]
    public async Task StartPaused_HoldsFirstOutputUntilResumeWithoutAudibleBlip()
    {
        var factory = new FakePlaybackOutputFactory();
        await using var service = new AudioPlaybackService(factory);

        var playback = service.PlaySlicesAsync(
            (_, _) => Task.FromResult(OneSecondWav()),
            [Slice("paused-start", TimeSpan.FromSeconds(1))],
            PlaybackSpeed.FromPercent(150),
            startPaused: true);
        var output = await factory.NextAsync();
        await output.Initialized.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, output.PlayCalls);
        Assert.Equal(TimeSpan.Zero, service.Elapsed);

        Assert.True(service.Resume().Succeeded);
        await output.Played.WaitAsync(TimeSpan.FromSeconds(5));
        output.Advance(TimeSpan.FromMilliseconds(200));
        Assert.InRange(
            service.Elapsed,
            TimeSpan.FromMilliseconds(299),
            TimeSpan.FromMilliseconds(301));

        output.Complete();
        await playback.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(75, 300)]
    [InlineData(100, 400)]
    [InlineData(125, 500)]
    [InlineData(150, 600)]
    [InlineData(200, 800)]
    public async Task Elapsed_AtEveryPlaybackSpeed_RemainsSourceTime(
        int percent,
        int expectedSourceMilliseconds)
    {
        var factory = new FakePlaybackOutputFactory();
        await using var service = new AudioPlaybackService(factory);
        var speed = PlaybackSpeed.FromPercent(percent);

        var playback = service.PlaySlicesAsync(
            (_, _) => Task.FromResult(OneSecondWav()),
            [Slice($"speed-{percent}", TimeSpan.FromSeconds(1))],
            speed,
            startPaused: false);
        var output = await factory.NextAsync();
        await output.Played.WaitAsync(TimeSpan.FromSeconds(5));

        output.Advance(TimeSpan.FromMilliseconds(400));

        Assert.InRange(
            service.Elapsed,
            TimeSpan.FromMilliseconds(expectedSourceMilliseconds - 1),
            TimeSpan.FromMilliseconds(expectedSourceMilliseconds + 1));
        output.Complete();
        await playback.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ReplacementAtNewSpeed_WaitsForOldOutputAndPreservesPauseIntent()
    {
        var factory = new FakePlaybackOutputFactory();
        await using var service = new AudioPlaybackService(factory);
        var first = service.PlaySlicesAsync(
            (_, _) => Task.FromResult(OneSecondWav()),
            [Slice("normal", TimeSpan.FromSeconds(1))],
            PlaybackSpeed.Normal,
            startPaused: false);
        var firstOutput = await factory.NextAsync();
        await firstOutput.Played.WaitAsync(TimeSpan.FromSeconds(5));
        firstOutput.Advance(TimeSpan.FromMilliseconds(250));

        var replacement = service.PlaySlicesAsync(
            (_, _) => Task.FromResult(OneSecondWav()),
            [Slice("double", TimeSpan.FromSeconds(1))],
            PlaybackSpeed.FromPercent(200),
            startPaused: true);
        var secondOutput = await factory.NextAsync();
        await secondOutput.Initialized.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(firstOutput.IsDisposed);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal(0, secondOutput.PlayCalls);
        Assert.Equal(TimeSpan.Zero, service.Elapsed);

        Assert.True(service.Resume().Succeeded);
        await secondOutput.Played.WaitAsync(TimeSpan.FromSeconds(5));
        secondOutput.Advance(TimeSpan.FromMilliseconds(250));
        Assert.InRange(
            service.Elapsed,
            TimeSpan.FromMilliseconds(499),
            TimeSpan.FromMilliseconds(501));
        secondOutput.Complete();
        await replacement.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RapidSpeedReplacements_LeaveOnlyLatestOutputActive()
    {
        var factory = new FakePlaybackOutputFactory();
        await using var service = new AudioPlaybackService(factory);

        var first = service.PlaySlicesAsync(
            (_, _) => Task.FromResult(OneSecondWav()),
            [Slice("first-speed", TimeSpan.FromSeconds(1))],
            PlaybackSpeed.Normal,
            startPaused: false);
        var firstOutput = await factory.NextAsync();
        await firstOutput.Played.WaitAsync(TimeSpan.FromSeconds(5));

        var second = service.PlaySlicesAsync(
            (_, _) => Task.FromResult(OneSecondWav()),
            [Slice("second-speed", TimeSpan.FromSeconds(1))],
            PlaybackSpeed.FromPercent(125),
            startPaused: false);
        var secondOutput = await factory.NextAsync();
        await secondOutput.Played.WaitAsync(TimeSpan.FromSeconds(5));

        var latest = service.PlaySlicesAsync(
            (_, _) => Task.FromResult(OneSecondWav()),
            [Slice("latest-speed", TimeSpan.FromSeconds(1))],
            PlaybackSpeed.FromPercent(200),
            startPaused: true);
        var latestOutput = await factory.NextAsync();
        await latestOutput.Initialized.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(firstOutput.IsDisposed);
        Assert.True(secondOutput.IsDisposed);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        Assert.False(latestOutput.IsDisposed);
        Assert.Equal(0, latestOutput.PlayCalls);

        Assert.True(service.Resume().Succeeded);
        await latestOutput.Played.WaitAsync(TimeSpan.FromSeconds(5));
        latestOutput.Advance(TimeSpan.FromMilliseconds(250));
        latestOutput.Complete();
        await latest.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.InRange(
            service.Elapsed,
            TimeSpan.FromMilliseconds(499),
            TimeSpan.FromMilliseconds(501));
    }

    [Fact]
    public async Task SpeedPipeline_BoundsSourceBeforeResampling()
    {
        var factory = new FakePlaybackOutputFactory();
        await using var service = new AudioPlaybackService(factory);

        var playback = service.PlaySlicesAsync(
            (_, _) => Task.FromResult(OneSecondWav()),
            [Slice("bounded", TimeSpan.FromMilliseconds(250))],
            PlaybackSpeed.FromPercent(200),
            startPaused: false);
        var output = await factory.NextAsync();
        await output.Played.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.InRange(output.ReadAheadBytes, 3_998, 4_002);

        output.Advance(TimeSpan.FromMilliseconds(125));
        output.Complete();
        await playback.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.InRange(
            service.Elapsed,
            TimeSpan.FromMilliseconds(249),
            TimeSpan.FromMilliseconds(251));
    }

    [Fact]
    public async Task StopAsync_WaitsUntilActiveOutputIsDisposed()
    {
        var factory = new FakePlaybackOutputFactory();
        await using var service = new AudioPlaybackService(factory);
        var playback = service.PlaySlicesAsync(
            (_, _) => Task.FromResult(OneSecondWav()),
            [Slice("active", TimeSpan.FromSeconds(1))]);
        var output = await factory.NextAsync();
        await output.Played.WaitAsync(TimeSpan.FromSeconds(5));

        await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(output.IsDisposed);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => playback);
    }

    [Fact]
    public async Task StopAsync_WhenOutputBackendThrows_StillCleansGenerationAndAllowsNextRun()
    {
        var factory = new FakePlaybackOutputFactory((output, ordinal) =>
        {
            if (ordinal != 1) return;
            output.ThrowOnGetPosition = true;
            output.ThrowOnStop = true;
        });
        await using var service = new AudioPlaybackService(factory);
        var first = service.PlaySlicesAsync(
            (_, _) => Task.FromResult(OneSecondWav()),
            [Slice("failing-output", TimeSpan.FromSeconds(1))]);
        var firstOutput = await factory.NextAsync();
        await firstOutput.Played.WaitAsync(TimeSpan.FromSeconds(5));

        await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(firstOutput.IsDisposed);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        var second = service.PlaySlicesAsync(
            (_, _) => Task.FromResult(OneSecondWav()),
            [Slice("replacement", TimeSpan.FromSeconds(1))]);
        var secondOutput = await factory.NextAsync();
        await secondOutput.Played.WaitAsync(TimeSpan.FromSeconds(5));
        secondOutput.Advance(TimeSpan.FromSeconds(1));
        secondOutput.Complete();
        await second.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(TimeSpan.FromSeconds(1), service.Elapsed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PauseOrResume_WhenOutputBackendThrows_ReturnsFailureAndCanStillStop(bool resume)
    {
        var factory = new FakePlaybackOutputFactory();
        await using var service = new AudioPlaybackService(factory);
        var playback = service.PlaySlicesAsync(
            (_, _) => Task.FromResult(OneSecondWav()),
            [Slice("control-failure", TimeSpan.FromSeconds(1))]);
        var output = await factory.NextAsync();
        await output.Played.WaitAsync(TimeSpan.FromSeconds(5));

        PlaybackControlResult result;
        if (resume)
        {
            Assert.True(service.Pause().Succeeded);
            output.ThrowOnPlay = true;
            result = service.Resume();
        }
        else
        {
            output.ThrowOnPause = true;
            result = service.Pause();
        }

        Assert.False(result.Succeeded);
        Assert.Contains("simulated output", result.ErrorMessage, StringComparison.Ordinal);
        await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(output.IsDisposed);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => playback);
    }

    private static AudioPlaybackSlice Slice(string id, TimeSpan duration) =>
        new(
            new ArchivedAudioChunk(
                id,
                "session",
                AudioSourceKind.Microphone,
                0,
                DateTimeOffset.UnixEpoch,
                duration,
                id + ".wav.aes",
                1),
            TimeSpan.Zero,
            duration);

    private static byte[] OneSecondWav() => WavPcm.CreateMono16(new byte[32_000]);

    private sealed class CountingWaveProvider(WaveFormat waveFormat, int availableBytes) : IWaveProvider
    {
        private int _remaining = availableBytes;
        public int BytesRead { get; private set; }
        public WaveFormat WaveFormat { get; } = waveFormat;

        public int Read(byte[] buffer, int offset, int count)
        {
            var read = Math.Min(count, _remaining);
            Array.Clear(buffer, offset, read);
            _remaining -= read;
            BytesRead += read;
            return read;
        }
    }

    private sealed class FakePlaybackOutputFactory : IPlaybackOutputFactory
    {
        private readonly Channel<FakePlaybackOutput> _outputs = Channel.CreateUnbounded<FakePlaybackOutput>();
        private readonly Action<FakePlaybackOutput, int>? _configure;
        private int _createdCount;
        public FakePlaybackOutputFactory(Action<FakePlaybackOutput, int>? configure = null) => _configure = configure;
        public int CreatedCount => Volatile.Read(ref _createdCount);

        public IPlaybackOutput Create()
        {
            var output = new FakePlaybackOutput();
            var ordinal = Interlocked.Increment(ref _createdCount);
            _configure?.Invoke(output, ordinal);
            _outputs.Writer.TryWrite(output);
            return output;
        }

        public Task<FakePlaybackOutput> NextAsync() =>
            _outputs.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class FakePlaybackOutput : IPlaybackOutput
    {
        private IWaveProvider? _provider;
        private long _position;
        private bool _stoppedRaised;
        private bool _readAheadCompleted;

        public event EventHandler<StoppedEventArgs>? PlaybackStopped;
        public PlaybackState PlaybackState { get; private set; } = PlaybackState.Stopped;
        public WaveFormat OutputWaveFormat { get; private set; } = new WaveFormat(16_000, 16, 1);
        public TaskCompletionSource InitializedSource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource PlayedSource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Initialized => InitializedSource.Task;
        public Task Played => PlayedSource.Task;
        public int PlayCalls { get; private set; }
        public int PauseCalls { get; private set; }
        public int ReadAheadBytes { get; private set; }
        public bool IsDisposed { get; private set; }
        public bool ThrowOnGetPosition { get; set; }
        public bool ThrowOnStop { get; set; }
        public bool ThrowOnPause { get; set; }
        public bool ThrowOnPlay { get; set; }

        public void Init(IWaveProvider provider)
        {
            _provider = provider;
            OutputWaveFormat = provider.WaveFormat;
            InitializedSource.TrySetResult();
        }

        public void Play()
        {
            if (ThrowOnPlay) throw new InvalidOperationException("simulated output play failure");
            PlayCalls++;
            PlaybackState = PlaybackState.Playing;
            if (!_readAheadCompleted)
            {
                _readAheadCompleted = true;
                var buffer = new byte[4_096];
                int read;
                while ((read = _provider!.Read(buffer, 0, buffer.Length)) > 0) ReadAheadBytes += read;
            }
            PlayedSource.TrySetResult();
        }

        public void Pause()
        {
            if (ThrowOnPause) throw new InvalidOperationException("simulated output pause failure");
            PauseCalls++;
            PlaybackState = PlaybackState.Paused;
        }

        public void Stop()
        {
            if (ThrowOnStop) throw new InvalidOperationException("simulated output stop failure");
            Complete();
        }

        public void Complete()
        {
            PlaybackState = PlaybackState.Stopped;
            if (_stoppedRaised) return;
            _stoppedRaised = true;
            PlaybackStopped?.Invoke(this, new StoppedEventArgs(null));
        }

        public long GetPosition() => ThrowOnGetPosition
            ? throw new InvalidOperationException("simulated output position failure")
            : Interlocked.Read(ref _position);

        public void Advance(TimeSpan elapsed)
        {
            var bytes = (long)Math.Floor(elapsed.TotalSeconds * OutputWaveFormat.AverageBytesPerSecond);
            bytes -= bytes % OutputWaveFormat.BlockAlign;
            Interlocked.Exchange(ref _position, bytes);
        }

        public void Dispose()
        {
            IsDisposed = true;
            PlaybackState = PlaybackState.Stopped;
        }
    }
}
