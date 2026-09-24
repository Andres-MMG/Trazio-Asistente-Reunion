using System.Diagnostics.CodeAnalysis;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Trazio.AsistenteReunion.App;

/// <summary>
/// A supported playback speed. Speed changes intentionally change pitch.
/// </summary>
public sealed record PlaybackSpeed
{
    private static readonly PlaybackSpeed ThreeQuarters = new(75, "0,75×");
    private static readonly PlaybackSpeed OneAndAQuarter = new(125, "1,25×");
    private static readonly PlaybackSpeed OneAndAHalf = new(150, "1,5×");
    private static readonly PlaybackSpeed Double = new(200, "2×");

    private PlaybackSpeed(int percent, string label)
    {
        Percent = percent;
        Label = label;
    }

    public static PlaybackSpeed Normal { get; } = new(100, "1×");

    public static IReadOnlyList<PlaybackSpeed> Catalog { get; } = Array.AsReadOnly(
        new[] { ThreeQuarters, Normal, OneAndAQuarter, OneAndAHalf, Double });

    public int Percent { get; }
    public string Label { get; }
    public double Multiplier => Percent / 100d;

    public static PlaybackSpeed FromPercent(int percent) =>
        TryCreate(percent, out var speed)
            ? speed
            : throw new ArgumentOutOfRangeException(
                nameof(percent),
                percent,
                "La velocidad debe ser 75, 100, 125, 150 o 200 por ciento.");

    public static bool TryCreate(int percent, [NotNullWhen(true)] out PlaybackSpeed? speed)
    {
        speed = percent switch
        {
            75 => ThreeQuarters,
            100 => Normal,
            125 => OneAndAQuarter,
            150 => OneAndAHalf,
            200 => Double,
            _ => null
        };
        return speed is not null;
    }
}

internal readonly record struct PlaybackSpeedChangeTicket(
    long ChangeGeneration,
    long PlaybackIntentGeneration);

internal sealed class PlaybackSpeedChangeCoordinator
{
    private readonly object _sync = new();
    private long _changeGeneration;
    private long _playbackIntentGeneration;
    private int _blockingTransitions;

    public bool IsPlaybackIntentTransitionActive
    {
        get
        {
            lock (_sync)
                return _blockingTransitions > 0;
        }
    }

    public bool TryBeginChange(out PlaybackSpeedChangeTicket ticket)
    {
        lock (_sync)
        {
            if (_blockingTransitions > 0)
            {
                ticket = default;
                return false;
            }

            ticket = new(++_changeGeneration, _playbackIntentGeneration);
            return true;
        }
    }

    public IDisposable BeginPlaybackIntentTransition()
    {
        lock (_sync)
        {
            _blockingTransitions++;
            InvalidatePlaybackIntentCore();
        }
        return new PlaybackIntentTransition(this);
    }

    public void InvalidatePlaybackIntent()
    {
        lock (_sync)
            InvalidatePlaybackIntentCore();
    }

    public bool IsCurrent(PlaybackSpeedChangeTicket ticket)
    {
        lock (_sync)
            return ticket.ChangeGeneration == _changeGeneration &&
                ticket.PlaybackIntentGeneration == _playbackIntentGeneration;
    }

    private void InvalidatePlaybackIntentCore()
    {
        _playbackIntentGeneration++;
        _changeGeneration++;
    }

    private void EndPlaybackIntentTransition()
    {
        lock (_sync)
        {
            if (_blockingTransitions <= 0)
                throw new InvalidOperationException("No hay una transición de reproducción activa.");
            _blockingTransitions--;
        }
    }

    private sealed class PlaybackIntentTransition(PlaybackSpeedChangeCoordinator owner) : IDisposable
    {
        private PlaybackSpeedChangeCoordinator? _owner = owner;

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.EndPlaybackIntentTransition();
    }
}

/// <summary>
/// Creates a bounded playback pipeline whose speed adjustment intentionally changes pitch.
/// The input must already be limited to the allowed source-audio range.
/// </summary>
internal static class PlaybackSpeedAudioPipeline
{
    public static PlaybackSpeedAudioPipelineResult Create(
        IWaveProvider boundedSource,
        PlaybackSpeed speed)
    {
        ArgumentNullException.ThrowIfNull(boundedSource);
        ArgumentNullException.ThrowIfNull(speed);

        var sourceSampleRate = boundedSource.WaveFormat.SampleRate;
        if (speed == PlaybackSpeed.Normal)
            return new(boundedSource, 1d, sourceSampleRate);

        var intermediateSampleRate = checked((int)Math.Round(
            sourceSampleRate / speed.Multiplier,
            MidpointRounding.AwayFromZero));
        if (intermediateSampleRate <= 0)
            throw new InvalidOperationException("La velocidad produjo una frecuencia de muestreo no válida.");

        var resampled = new WdlResamplingSampleProvider(
            boundedSource.ToSampleProvider(),
            intermediateSampleRate);
        var relabeled = new SourceRateRelabelingSampleProvider(resampled, sourceSampleRate);
        var output = new SampleToWaveProvider16(relabeled);
        var sourceTimeScale = sourceSampleRate / (double)intermediateSampleRate;
        return new(output, sourceTimeScale, intermediateSampleRate);
    }

    private sealed class SourceRateRelabelingSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;

        public SourceRateRelabelingSampleProvider(ISampleProvider source, int outputSampleRate)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(
                outputSampleRate,
                source.WaveFormat.Channels);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count) =>
            _source.Read(buffer, offset, count);
    }
}

internal sealed record PlaybackSpeedAudioPipelineResult(
    IWaveProvider Output,
    double SourceTimeScale,
    int IntermediateSampleRate)
{
    public WaveFormat OutputFormat => Output.WaveFormat;
}
