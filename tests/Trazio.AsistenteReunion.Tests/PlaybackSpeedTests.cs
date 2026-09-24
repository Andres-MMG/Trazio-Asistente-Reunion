using NAudio.Wave;
using Trazio.AsistenteReunion.App;

namespace Trazio.AsistenteReunion.Tests;

public sealed class PlaybackSpeedTests
{
    [Fact]
    public void Catalog_ContainsSupportedSpeedsInDisplayOrder()
    {
        Assert.Equal([75, 100, 125, 150, 200], PlaybackSpeed.Catalog.Select(speed => speed.Percent));
        Assert.Equal(["0,75×", "1×", "1,25×", "1,5×", "2×"], PlaybackSpeed.Catalog.Select(speed => speed.Label));
        Assert.Equal(PlaybackSpeed.FromPercent(100), PlaybackSpeed.Normal);
    }

    [Theory]
    [InlineData(75, 0.75)]
    [InlineData(100, 1.00)]
    [InlineData(125, 1.25)]
    [InlineData(150, 1.50)]
    [InlineData(200, 2.00)]
    public void FromPercent_SupportedValue_ReturnsStableValueObject(int percent, double multiplier)
    {
        var first = PlaybackSpeed.FromPercent(percent);
        var second = PlaybackSpeed.FromPercent(percent);

        Assert.Equal(first, second);
        Assert.Same(first, second);
        Assert.Equal(multiplier, first.Multiplier);
        Assert.True(PlaybackSpeed.TryCreate(percent, out var created));
        Assert.Same(first, created);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(74)]
    [InlineData(76)]
    [InlineData(175)]
    [InlineData(201)]
    public void UnsupportedPercent_IsRejected(int percent)
    {
        Assert.False(PlaybackSpeed.TryCreate(percent, out var speed));
        Assert.Null(speed);
        Assert.Throws<ArgumentOutOfRangeException>(() => PlaybackSpeed.FromPercent(percent));
    }

    [Fact]
    public void Pipeline_NormalSpeed_IsARealBypass()
    {
        var source = OneSecondSource();

        var pipeline = PlaybackSpeedAudioPipeline.Create(source, PlaybackSpeed.Normal);

        Assert.Same(source, pipeline.Output);
        Assert.Equal(1d, pipeline.SourceTimeScale);
        Assert.Equal(SourceSampleRate, pipeline.IntermediateSampleRate);
        Assert.Same(source.WaveFormat, pipeline.OutputFormat);
    }

    [Theory]
    [InlineData(75)]
    [InlineData(100)]
    [InlineData(125)]
    [InlineData(150)]
    [InlineData(200)]
    public void Pipeline_ProducesExpectedFramesAndEffectiveSourceScale(int percent)
    {
        var source = OneSecondSource();
        var speed = PlaybackSpeed.FromPercent(percent);

        var pipeline = PlaybackSpeedAudioPipeline.Create(source, speed);
        var outputFrames = ReadAllFrames(pipeline.Output);
        var expectedIntermediateRate = (int)Math.Round(
            SourceSampleRate / speed.Multiplier,
            MidpointRounding.AwayFromZero);

        Assert.Equal(SourceSampleRate, pipeline.OutputFormat.SampleRate);
        Assert.Equal(SourceChannels, pipeline.OutputFormat.Channels);
        Assert.Equal(expectedIntermediateRate, pipeline.IntermediateSampleRate);
        Assert.Equal(
            SourceSampleRate / (double)expectedIntermediateRate,
            pipeline.SourceTimeScale,
            precision: 12);
        Assert.InRange(outputFrames, expectedIntermediateRate - 1, expectedIntermediateRate + 1);
    }

    [Fact]
    public void ChangeCoordinator_StopOrSessionChangeInvalidatesPendingRestart()
    {
        var coordinator = new PlaybackSpeedChangeCoordinator();
        Assert.True(coordinator.TryBeginChange(out var pendingRestart));

        coordinator.InvalidatePlaybackIntent();

        Assert.False(coordinator.IsCurrent(pendingRestart));
    }

    [Fact]
    public void ChangeCoordinator_RapidChangesKeepOnlyLatestRestartCurrent()
    {
        var coordinator = new PlaybackSpeedChangeCoordinator();
        Assert.True(coordinator.TryBeginChange(out var first));
        Assert.True(coordinator.TryBeginChange(out var latest));

        Assert.False(coordinator.IsCurrent(first));
        Assert.True(coordinator.IsCurrent(latest));
    }

    [Fact]
    public void ChangeCoordinator_RejectsRestartStartedDuringDestructiveTransition()
    {
        var coordinator = new PlaybackSpeedChangeCoordinator();
        Assert.True(coordinator.TryBeginChange(out var queuedBeforeTransition));

        using (coordinator.BeginPlaybackIntentTransition())
        {
            Assert.True(coordinator.IsPlaybackIntentTransitionActive);
            Assert.False(coordinator.IsCurrent(queuedBeforeTransition));
            Assert.False(coordinator.TryBeginChange(out _));
        }

        Assert.False(coordinator.IsPlaybackIntentTransitionActive);
        Assert.True(coordinator.TryBeginChange(out var nextPlaybackChange));
        Assert.True(coordinator.IsCurrent(nextPlaybackChange));
    }

    [Fact]
    public void ChangeCoordinator_OverlappingDestructiveTransitionsKeepRestartBlockedUntilBothEnd()
    {
        var coordinator = new PlaybackSpeedChangeCoordinator();
        var first = coordinator.BeginPlaybackIntentTransition();
        var second = coordinator.BeginPlaybackIntentTransition();

        first.Dispose();
        Assert.True(coordinator.IsPlaybackIntentTransitionActive);
        Assert.False(coordinator.TryBeginChange(out _));

        second.Dispose();
        Assert.False(coordinator.IsPlaybackIntentTransitionActive);
        Assert.True(coordinator.TryBeginChange(out _));
    }

    private const int SourceSampleRate = 16_000;
    private const int SourceChannels = 1;

    private static IWaveProvider OneSecondSource() =>
        new DeterministicPcm16WaveProvider(SourceSampleRate, SourceChannels, SourceSampleRate);

    private static int ReadAllFrames(IWaveProvider provider)
    {
        var buffer = new byte[4_096];
        var bytes = 0;
        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0) bytes += read;
        Assert.Equal(0, bytes % provider.WaveFormat.BlockAlign);
        return bytes / provider.WaveFormat.BlockAlign;
    }

    private sealed class DeterministicPcm16WaveProvider(
        int sampleRate,
        int channels,
        int frameCount) : IWaveProvider
    {
        private int _remainingBytes = checked(frameCount * channels * sizeof(short));

        public WaveFormat WaveFormat { get; } = new(sampleRate, 16, channels);

        public int Read(byte[] buffer, int offset, int count)
        {
            var read = Math.Min(count, _remainingBytes);
            read -= read % WaveFormat.BlockAlign;
            if (read <= 0) return 0;
            Array.Clear(buffer, offset, read);
            _remainingBytes -= read;
            return read;
        }
    }
}
