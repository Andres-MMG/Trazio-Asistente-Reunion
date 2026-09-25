using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.App;

public sealed record AudioDevice(string Id, string Name);
public sealed record CapturedSecond(AudioSourceKind Source, byte[] Pcm16, float Peak,
    string? CaptureRunId = null, long? FirstSourceSample = null,
    long? CapturedMonotonicTimestamp = null);

public interface IAudioCaptureService : IAsyncDisposable
{
    event EventHandler<CapturedSecond>? SecondCaptured;
    event EventHandler<string>? CaptureFailed;
    void Start(string? microphoneId, string? outputId, bool useMicrophone, bool useOutput);
    Task StopAsync();
}

public sealed class AudioCaptureService : IAudioCaptureService
{
    private readonly List<CaptureTrack> _tracks = [];
    public event EventHandler<CapturedSecond>? SecondCaptured;
    public event EventHandler<string>? CaptureFailed;

    public static IReadOnlyList<AudioDevice> GetMicrophones() => Enumerate(DataFlow.Capture);
    public static IReadOnlyList<AudioDevice> GetOutputs() => Enumerate(DataFlow.Render);

    public void Start(string? microphoneId, string? outputId, bool useMicrophone, bool useOutput)
    {
        StopAsync().GetAwaiter().GetResult();
        using var enumerator = new MMDeviceEnumerator();
        if (useMicrophone)
        {
            if (string.IsNullOrWhiteSpace(microphoneId)) throw new InvalidOperationException("Selecciona un micrófono.");
            var device = enumerator.GetDevice(microphoneId);
            AddTrack(new WasapiCapture(device), AudioSourceKind.Microphone);
        }
        if (useOutput)
        {
            if (string.IsNullOrWhiteSpace(outputId)) throw new InvalidOperationException("Selecciona un dispositivo de salida.");
            var device = enumerator.GetDevice(outputId);
            AddTrack(new WasapiLoopbackCapture(device), AudioSourceKind.SystemOutput);
        }
        if (_tracks.Count == 0) throw new InvalidOperationException("Activa al menos una fuente de audio.");
        foreach (var track in _tracks) track.Start();
    }

    public async Task StopAsync()
    {
        var tracks = _tracks.ToArray();
        _tracks.Clear();
        foreach (var track in tracks) await track.DisposeAsync();
    }

    private void AddTrack(WasapiCapture capture, AudioSourceKind source)
    {
        var track = new CaptureTrack(capture, source);
        track.SecondCaptured += (_, e) => SecondCaptured?.Invoke(this, e);
        track.Failed += (_, e) => CaptureFailed?.Invoke(this, e);
        _tracks.Add(track);
    }

    private static IReadOnlyList<AudioDevice> Enumerate(DataFlow flow)
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active)
            .Select(d => new AudioDevice(d.ID, d.FriendlyName)).ToArray();
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private sealed class CaptureTrack(WasapiCapture capture, AudioSourceKind source) : IAsyncDisposable
    {
        private readonly BufferedWaveProvider _buffer = new(capture.WaveFormat)
            { DiscardOnBufferOverflow = false, BufferDuration = TimeSpan.FromSeconds(5) };
        private CancellationTokenSource? _cts;
        private Task? _pump;
        private readonly object _bufferGate = new();
        private readonly SemaphoreSlim _samplesAvailable = new(0, 1);
        private string _captureRunId = Guid.NewGuid().ToString("N");
        private long _captureGeneration;
        private long _lastDataTimestamp;
        private int _lastPacketBytes;
        private bool _captureFaulted;
        private long _nextSourceSample;
        public event EventHandler<CapturedSecond>? SecondCaptured;
        public event EventHandler<string>? Failed;

        public void Start()
        {
            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += OnRecordingStopped;
            _cts = new();
            _pump = Task.Run(() => PumpAsync(_cts.Token));
            capture.StartRecording();
        }

        private void OnDataAvailable(object? sender, WaveInEventArgs e)
        {
            string? failure = null;
            try
            {
                lock (_bufferGate)
                {
                    if (_captureFaulted || e.BytesRecorded <= 0) return;
                    var now = Stopwatch.GetTimestamp();
                    if (_lastDataTimestamp != 0)
                    {
                        var action = CapturePacketContinuity.Decide(
                            Stopwatch.GetElapsedTime(_lastDataTimestamp, now), _lastPacketBytes,
                            e.BytesRecorded, capture.WaveFormat.AverageBytesPerSecond,
                            _buffer.BufferedBytes);
                        if (action == CaptureGapAction.Fail)
                        {
                            _captureFaulted = true;
                            failure = "Se detectó un silencio prolongado con audio parcial pendiente. " +
                                "La captura se pausó para evitar mezclar audio de momentos distintos.";
                        }
                        else if (action == CaptureGapAction.Rotate)
                        {
                            _captureRunId = Guid.NewGuid().ToString("N");
                            _captureGeneration++;
                        }
                    }
                    _lastDataTimestamp = now;
                    _lastPacketBytes = e.BytesRecorded;
                    if (failure is null)
                    {
                        _buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
                        if (_samplesAvailable.CurrentCount == 0) _samplesAvailable.Release();
                    }
                }
            }
            catch (Exception ex)
            {
                lock (_bufferGate) _captureFaulted = true;
                failure = ex.Message;
            }
            if (failure is not null)
                Failed?.Invoke(this, $"{(source == AudioSourceKind.Microphone ? "Micrófono" : "Audio del equipo")}: {failure}");
        }

        private void OnRecordingStopped(object? sender, StoppedEventArgs e)
        {
            if (e.Exception is not null) Failed?.Invoke(this, $"{(source == AudioSourceKind.Microphone ? "Micrófono" : "Audio del equipo")}: {e.Exception.Message}");
        }

        private ISampleProvider CreateSampleProvider()
        {
            ISampleProvider samples = _buffer.ToSampleProvider();
            if (samples.WaveFormat.Channels > 1)
                samples = new StereoToMonoSampleProvider(samples) { LeftVolume = 0.5f, RightVolume = 0.5f };
            return new WdlResamplingSampleProvider(samples, 16_000);
        }

        private async Task PumpAsync(CancellationToken cancellationToken)
        {
            ISampleProvider? samples = null;
            long observedGeneration = -1;
            var floats = new float[16_000];
            while (!cancellationToken.IsCancellationRequested)
            {
                var waitForAudio = false;
                CapturedSecond? capturedSecond = null;
                lock (_bufferGate)
                {
                    if (_captureFaulted || _buffer.BufferedBytes < capture.WaveFormat.AverageBytesPerSecond)
                    {
                        waitForAudio = true;
                    }
                    else
                    {
                        if (observedGeneration != _captureGeneration)
                        {
                            samples = CreateSampleProvider();
                            observedGeneration = _captureGeneration;
                        }
                        var read = samples!.Read(floats, 0, floats.Length);
                        if (read == 0)
                        {
                            waitForAudio = true;
                        }
                        else
                        {
                            var pcm = new byte[read * 2];
                            var peak = 0f;
                            for (var i = 0; i < read; i++)
                            {
                                var sample = Math.Clamp(floats[i], -1f, 1f);
                                peak = Math.Max(peak, Math.Abs(sample));
                                var value = (short)(sample * short.MaxValue);
                                pcm[i * 2] = (byte)value;
                                pcm[i * 2 + 1] = (byte)(value >> 8);
                            }
                            var firstSourceSample = _nextSourceSample;
                            _nextSourceSample = checked(_nextSourceSample + read);
                            capturedSecond = new(source, pcm, peak, _captureRunId, firstSourceSample,
                                Stopwatch.GetTimestamp());
                        }
                    }
                }
                if (capturedSecond is not null) SecondCaptured?.Invoke(this, capturedSecond);
                if (waitForAudio) await _samplesAvailable.WaitAsync(cancellationToken);
            }
        }

        public async ValueTask DisposeAsync()
        {
            capture.StopRecording();
            _cts?.Cancel();
            if (_pump is not null)
            {
                try { await _pump; } catch (OperationCanceledException) { }
            }
            capture.Dispose();
            _samplesAvailable.Dispose();
            _cts?.Dispose();
        }
    }
}

internal enum CaptureGapAction { Continue, Rotate, Fail }

internal static class CapturePacketContinuity
{
    internal static CaptureGapAction Decide(
        TimeSpan elapsed, int previousBytes, int currentBytes, int bytesPerSecond, int bufferedBytes)
    {
        if (previousBytes < 0 || currentBytes < 0 || bytesPerSecond <= 0 || bufferedBytes < 0)
            return CaptureGapAction.Fail;
        // The current callback contains the audio delivered since the prior callback;
        // a larger prior packet must not conceal a new silent interval.
        var packetDuration = TimeSpan.FromSeconds(currentBytes / (double)bytesPerSecond);
        if (elapsed <= packetDuration + AudioContinuityPolicy.MaximumClockDrift)
            return CaptureGapAction.Continue;
        return bufferedBytes > 0 ? CaptureGapAction.Fail : CaptureGapAction.Rotate;
    }
}

internal sealed class AudioLevelSnapshot
{
    private int _microphoneBits;
    private int _outputBits;

    public void Publish(CapturedSecond captured)
    {
        var bits = BitConverter.SingleToInt32Bits(Math.Clamp(captured.Peak, 0f, 1f));
        if (captured.Source == AudioSourceKind.Microphone)
            Interlocked.Exchange(ref _microphoneBits, bits);
        else
            Interlocked.Exchange(ref _outputBits, bits);
    }

    public (float Microphone, float Output) Read() =>
        (BitConverter.Int32BitsToSingle(Volatile.Read(ref _microphoneBits)),
         BitConverter.Int32BitsToSingle(Volatile.Read(ref _outputBits)));
}
