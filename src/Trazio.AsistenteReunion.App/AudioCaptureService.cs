using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.App;

public sealed record AudioDevice(string Id, string Name);
public sealed record CapturedSecond(AudioSourceKind Source, byte[] Pcm16, float Peak);

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
            try { _buffer.AddSamples(e.Buffer, 0, e.BytesRecorded); }
            catch (Exception ex) { Failed?.Invoke(this, $"{(source == AudioSourceKind.Microphone ? "Micrófono" : "Audio del equipo")}: {ex.Message}"); }
        }

        private void OnRecordingStopped(object? sender, StoppedEventArgs e)
        {
            if (e.Exception is not null) Failed?.Invoke(this, $"{(source == AudioSourceKind.Microphone ? "Micrófono" : "Audio del equipo")}: {e.Exception.Message}");
        }

        private async Task PumpAsync(CancellationToken cancellationToken)
        {
            ISampleProvider samples = _buffer.ToSampleProvider();
            if (samples.WaveFormat.Channels > 1)
                samples = new StereoToMonoSampleProvider(samples) { LeftVolume = 0.5f, RightVolume = 0.5f };
            samples = new WdlResamplingSampleProvider(samples, 16_000);
            var floats = new float[16_000];
            while (!cancellationToken.IsCancellationRequested)
            {
                if (_buffer.BufferedBytes < capture.WaveFormat.AverageBytesPerSecond)
                {
                    await Task.Delay(50, cancellationToken);
                    continue;
                }
                var read = samples.Read(floats, 0, floats.Length);
                if (read == 0) continue;
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
                SecondCaptured?.Invoke(this, new(source, pcm, peak));
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
            _cts?.Dispose();
        }
    }
}
