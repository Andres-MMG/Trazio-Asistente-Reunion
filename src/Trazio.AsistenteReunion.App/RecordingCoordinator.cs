using System.Threading.Channels;
using System.IO;
using System.Collections.Concurrent;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.App;

public sealed record SourceDiagnostic(
    AudioSourceKind Source, DateTimeOffset? LastPcmAt, double CapturedSeconds,
    int ArchivedChunks, int PendingTranscription, string? LatestError, bool AudioArchiveEnabled, bool IsStalled);

public sealed class RecordingCoordinator : IAsyncDisposable
{
    private readonly IAudioCaptureService _capture;
    private readonly SqliteSessionStore _store;
    private readonly PendingAudioQueue _pendingQueue;
    private readonly ITranscriptionTransportFactory _transportFactory;
    private readonly AudioArchiveStore? _audioArchiveStore;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<AudioSourceKind, List<AudioChunk>> _windows = [];
    private readonly Dictionary<AudioSourceKind, long> _sequences = [];
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _captureGate = new();
    private Channel<CapturedAudioEnvelope>? _ingestion;
    private CancellationTokenSource? _consumerCts;
    private Task? _ingestionPump;
    private Task? _consumerPump;
    private ITranscriptionTransport? _transport;
    private MeetingSession? _session;
    private SessionTimeline? _sessionTimeline;
    private SessionTimelineContext? _activeTimelineContext;
    private DateTimeOffset _sessionStart;
    private string? _modelPath;
    private string _language = "es";
    private string? _localSpeakerName;
    private bool _accepting;
    private bool _paused;
    private bool _drainFailed;
    private int _inFlight;
    private int _workerRestartCount;
    private AudioArchiveSession? _audioArchive;
    private long _revision;
    private long _audioBudgetBytes;
    private EventHandler<CapturedSecond>? _captureHandler;
    private EventHandler<string>? _failureHandler;
    private EventHandler<ArchivedAudioChunk>? _archiveHandler;
    private readonly ConcurrentDictionary<AudioSourceKind, SourceDiagnosticState> _diagnostics = [];
    private Task? _diagnosticPump;

    public RecordingCoordinator(
        IAudioCaptureService capture,
        SqliteSessionStore store,
        PendingAudioQueue pendingQueue,
        ITranscriptionTransportFactory? transportFactory = null,
        AudioArchiveStore? audioArchiveStore = null,
        TimeProvider? timeProvider = null)
    {
        _capture = capture;
        _store = store;
        _pendingQueue = pendingQueue;
        _transportFactory = transportFactory ?? new ProcessTranscriptionTransportFactory();
        _audioArchiveStore = audioArchiveStore;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public event EventHandler<string>? StatusChanged;
    public event EventHandler<TranscriptSegment>? SegmentReady;
    public event EventHandler<CapturedSecond>? LevelChanged;
    public event EventHandler<SourceDiagnostic>? DiagnosticChanged;
    public string? ActiveSessionId => _session?.Id;
    internal ISessionTimelineContext? ActiveTimelineContext => Volatile.Read(ref _activeTimelineContext);

    public async Task StartAsync(string title, AppSettings settings, CancellationToken cancellationToken = default, string? localDisplayNameOverride = null, MeetingProvider meetingProvider = MeetingProvider.NotSelected)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            if (_session is not null) throw new InvalidOperationException("Ya hay una sesión activa.");
            ValidateSettings(settings);
            ResetSessionState();
            var revision = ++_revision;
            var timeline = new SessionTimeline(_timeProvider);
            _sessionStart = timeline.StartedAtUtc;
            _modelPath = settings.ModelPath;
            _language = settings.Language;
            _localSpeakerName = settings.CaptureMicrophone
                ? LocalProfile.ResolveMeetingDisplayName(settings, localDisplayNameOverride)
                : null;
            _session = new(Guid.NewGuid().ToString("N"),
                string.IsNullOrWhiteSpace(title) ? $"Reunión {_sessionStart:yyyy-MM-dd HH:mm}" : title.Trim(),
                _sessionStart, null, SessionState.Recording, _localSpeakerName, meetingProvider);
            await _store.CreateSessionAsync(_session, cancellationToken);
            if (settings.CaptureMicrophone) GetDiagnostic(AudioSourceKind.Microphone);
            if (settings.CaptureSystemOutput) GetDiagnostic(AudioSourceKind.SystemOutput);
            if (settings.KeepEncryptedAudio)
            {
                _audioBudgetBytes = settings.AudioStorageBudgetGb * 1024L * 1024 * 1024;
                _audioArchive = _audioArchiveStore?.CreateSession(_session.Id, _audioBudgetBytes)
                    ?? throw new InvalidOperationException("El almacenamiento cifrado de audio no está disponible.");
                _archiveHandler = (_, chunk) => OnArchiveChunkCommitted(revision, chunk);
                _audioArchive.ChunkCommitted += _archiveHandler;
            }

            try
            {
                _transport = await _transportFactory.StartAsync(_modelPath!, _language, cancellationToken);
                _sessionTimeline = timeline;
                StartPumps(revision, cancellationToken);
                _captureHandler = (_, captured) => OnSecondCaptured(revision, captured);
                _failureHandler = (_, error) => OnCaptureFailed(revision, error);
                _capture.SecondCaptured += _captureHandler;
                _capture.CaptureFailed += _failureHandler;
                lock (_captureGate)
                {
                    Volatile.Write(
                        ref _activeTimelineContext,
                        new SessionTimelineContext(_session.Id, revision, timeline));
                    Volatile.Write(ref _accepting, true);
                }
                _capture.Start(settings.MicrophoneDeviceId, settings.OutputDeviceId, settings.CaptureMicrophone, settings.CaptureSystemOutput);
                StatusChanged?.Invoke(this, "Grabando");
            }
            catch
            {
                await RollBackFailedStartAsync(cancellationToken);
                throw;
            }
        }
        finally { _lifecycle.Release(); }
    }

    public void Pause()
    {
        lock (_captureGate) _paused = true;
        StatusChanged?.Invoke(this, "Pausada");
    }

    public void Resume()
    {
        lock (_captureGate)
        {
            if (_drainFailed) return;
            _paused = false;
        }
        StatusChanged?.Invoke(this, "Grabando");
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            if (_session is null) return;
            var sessionId = _session.Id;
            try
            {
                await StopAcceptingAndDrainAsync(cancellationToken);
                if (_audioArchive is not null) await _audioArchive.CompleteAsync(cancellationToken);
                if (_drainFailed) throw new InvalidOperationException("No se pudo transcribir todo el audio recibido.");
                await _store.CompleteSessionAsync(sessionId, SessionState.Completed, DateTimeOffset.UtcNow, cancellationToken);
                if (_audioArchiveStore is not null && _audioArchive is not null)
                    await _audioArchiveStore.PruneAsync(_audioBudgetBytes, cancellationToken);
                StatusChanged?.Invoke(this, "Detenida");
            }
            catch
            {
                await _store.CompleteSessionAsync(sessionId, SessionState.Interrupted, DateTimeOffset.UtcNow, CancellationToken.None);
                if (_audioArchiveStore is not null && _audioArchive is not null)
                    await _audioArchiveStore.PruneAsync(_audioBudgetBytes, CancellationToken.None);
                StatusChanged?.Invoke(this, "Interrumpida: se conservó el audio cifrado pendiente para recuperarlo");
                throw;
            }
            finally
            {
                await StopPumpsAfterFailureAsync();
                await DisposeTransportAsync(CancellationToken.None);
                if (_audioArchive is not null) await _audioArchive.DisposeAsync();
                ResetAfterSession();
            }
        }
        finally { _lifecycle.Release(); }
    }

    public async Task RecoverAsync(SessionSummary session, AppSettings settings, IReadOnlyList<AudioChunk> pending, CancellationToken cancellationToken = default)
    {
        if (pending.Count == 0) return;
        ValidateSettings(settings);
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            if (_session is not null) throw new InvalidOperationException("Ya hay una sesión activa.");
            ResetSessionState();
            _session = new(session.Id, session.Title, session.StartedAt, session.EndedAt, SessionState.Interrupted, session.LocalSpeakerName, session.MeetingProvider);
            _localSpeakerName = session.LocalSpeakerName;
            _sessionStart = session.StartedAt;
            _modelPath = settings.ModelPath;
            _language = settings.Language;
            try
            {
                _transport = await _transportFactory.StartAsync(_modelPath!, _language, cancellationToken);
                foreach (var chunk in pending.OrderBy(c => c.Source).ThenBy(c => c.Sequence))
                    await AddChunkToWindowAsync(chunk, cancellationToken);
                await FlushWindowsAsync(cancellationToken);
                if (_drainFailed) throw new InvalidOperationException("No se completó la recuperación del audio pendiente.");
                await _store.CompleteSessionAsync(session.Id, SessionState.Interrupted, DateTimeOffset.UtcNow, cancellationToken);
                StatusChanged?.Invoke(this, "Se recuperó la transcripción pendiente de una sesión interrumpida");
            }
            finally
            {
                await DisposeTransportAsync(CancellationToken.None);
                ResetAfterSession();
            }
        }
        finally { _lifecycle.Release(); }
    }

    private void StartPumps(long revision, CancellationToken cancellationToken)
    {
        _ingestion = Channel.CreateBounded<CapturedAudioEnvelope>(new BoundedChannelOptions(10)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
        _consumerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ingestionPump = Task.Run(() => IngestAsync(_consumerCts.Token), _consumerCts.Token);
        _consumerPump = Task.Run(() => ConsumePendingAsync(revision, _consumerCts.Token), _consumerCts.Token);
        _diagnosticPump = Task.Run(() => PublishDiagnosticsAsync(revision, _consumerCts.Token), _consumerCts.Token);
    }

    private void OnSecondCaptured(long revision, CapturedSecond captured)
    {
        SessionTimelineContext context;
        ChannelWriter<CapturedAudioEnvelope>? writer;
        bool admittedAtEntry;
        TimeSpan offset;
        lock (_captureGate)
        {
            context = Volatile.Read(ref _activeTimelineContext)!;
            if (revision != Volatile.Read(ref _revision) ||
                context is null ||
                context.Revision != revision ||
                !context.TryGetCurrentOffset(out offset))
                return;
            writer = _ingestion?.Writer;
            admittedAtEntry = !_paused && Volatile.Read(ref _accepting);
        }

        var envelope = new CapturedAudioEnvelope(revision, context.SessionId, offset, captured);
        LevelChanged?.Invoke(this, captured);
        lock (_captureGate)
        {
            if (!admittedAtEntry ||
                !ReferenceEquals(Volatile.Read(ref _activeTimelineContext), context) ||
                revision != Volatile.Read(ref _revision) ||
                _paused ||
                !Volatile.Read(ref _accepting))
                return;
            if (writer?.TryWrite(envelope) == true) return;
            Volatile.Write(ref _accepting, false);
            _paused = true;
            _drainFailed = true;
            RevokeActiveTimelineContextLocked();
        }
        StatusChanged?.Invoke(this, "Pausada: se alcanzó el límite de cinco segundos de captura en memoria");
    }

    private void OnCaptureFailed(long revision, string error)
    {
        lock (_captureGate)
        {
            if (revision != Volatile.Read(ref _revision)) return;
            Volatile.Write(ref _accepting, false);
            _paused = true;
            RevokeActiveTimelineContextLocked();
        }
        foreach (var source in Enum.GetValues<AudioSourceKind>())
            PublishDiagnostic(source, error);
        StatusChanged?.Invoke(this, $"Pausada: el dispositivo de audio se desconectó ({error})");
    }

    private async Task IngestAsync(CancellationToken cancellationToken)
    {
        await foreach (var envelope in _ingestion!.Reader.ReadAllAsync(cancellationToken))
        {
            var session = _session;
            var timeline = _sessionTimeline;
            if (session is null ||
                timeline is null ||
                envelope.Revision != Volatile.Read(ref _revision) ||
                !string.Equals(envelope.SessionId, session.Id, StringComparison.Ordinal))
                continue;
            var captured = envelope.Captured;
            var sequence = _sequences.GetValueOrDefault(captured.Source);
            _sequences[captured.Source] = sequence + 1;
            var chunk = AudioChunk.Create(
                session.Id,
                captured.Source,
                sequence,
                timeline.ToUtc(envelope.Offset),
                captured.Pcm16);
            try
            {
                var state = GetDiagnostic(captured.Source);
                state.LastPcmAt = chunk.CapturedAt;
                state.CapturedSeconds += captured.Pcm16.Length / 2d / 16_000;
                if (_audioArchive is not null)
                    await _audioArchive.AppendAsync(new(captured.Source, captured.Pcm16, chunk.CapturedAt), cancellationToken);
                await _store.SavePendingAsync(chunk, DateTimeOffset.UtcNow.AddHours(24), cancellationToken);
                if (!_pendingQueue.TryEnqueue(chunk))
                {
                    _drainFailed = true;
                    InvalidateActiveTimeline(envelope.Revision, envelope.SessionId);
                    StatusChanged?.Invoke(this, "Pausada: se alcanzó el límite de audio cifrado pendiente");
                }
                else state.PendingTranscription++;
                PublishDiagnostic(captured.Source);
            }
            catch (Exception ex)
            {
                _drainFailed = true;
                InvalidateActiveTimeline(envelope.Revision, envelope.SessionId);
                StatusChanged?.Invoke(this, "Pausada: no se pudo guardar el audio de forma segura");
                PublishDiagnostic(captured.Source, ex.Message);
                throw;
            }
        }
    }

    private async Task ConsumePendingAsync(long revision, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var lease = await _pendingQueue.LeaseAsync(cancellationToken);
            var chunk = lease.Chunk;
            Interlocked.Increment(ref _inFlight);
            try
            {
                await AddChunkToWindowAsync(chunk, cancellationToken);
                lease.Complete();
                var diagnostic = GetDiagnostic(chunk.Source);
                diagnostic.PendingTranscription = Math.Max(0, diagnostic.PendingTranscription - 1);
                PublishDiagnostic(chunk.Source);
            }
            catch
            {
                lease.Abandon();
                _drainFailed = true;
                InvalidateActiveTimeline(revision, chunk.SessionId);
                throw;
            }
            finally { Interlocked.Decrement(ref _inFlight); }
        }
    }

    private async Task AddChunkToWindowAsync(AudioChunk chunk, CancellationToken cancellationToken)
    {
        var window = _windows.GetValueOrDefault(chunk.Source);
        if (window is null) _windows[chunk.Source] = window = [];
        window.Add(chunk);
        if (window.Count >= 15) await ProcessWindowAsync(chunk.Source, window, false, cancellationToken);
    }

    private async Task ProcessWindowAsync(AudioSourceKind source, List<AudioChunk> window, bool final, CancellationToken cancellationToken)
    {
        if (_session is null || window.Count == 0) return;
        var chunks = window.ToArray();
        var pcm = chunks.SelectMany(c => c.Pcm16).ToArray();
        var workId = $"{_session.Id}:{source}:{chunks[0].Sequence}-{chunks[^1].Sequence}";
        var response = await TranscribeWithOneRestartAsync(workId, pcm, cancellationToken);
        if (!response.Success)
        {
            _drainFailed = true;
            InvalidateActiveTimeline(Volatile.Read(ref _revision), chunks[0].SessionId);
            StatusChanged?.Invoke(this, $"Pausada: falló la transcripción ({response.Error})");
            return;
        }

        var hasOverlap = chunks[0].Sequence > 0;
        foreach (var item in response.Segments ?? [])
        {
            if (string.IsNullOrWhiteSpace(item.Text) || (hasOverlap && item.EndMilliseconds <= 1_000)) continue;
            var offset = chunks[0].CapturedAt - _sessionStart;
            var id = $"{workId}:{item.StartMilliseconds}";
            var segment = new TranscriptSegment(id, _session.Id, source, chunks[0].Sequence,
                offset + TimeSpan.FromMilliseconds(item.StartMilliseconds),
                offset + TimeSpan.FromMilliseconds(item.EndMilliseconds), item.Text, DateTimeOffset.UtcNow,
                source == AudioSourceKind.Microphone ? _localSpeakerName : null);
            if (await _store.SaveSegmentAsync(segment, cancellationToken)) SegmentReady?.Invoke(this, segment);
        }

        var completedChunks = final ? chunks : chunks.Take(chunks.Length - 1);
        foreach (var chunk in completedChunks) await _store.DeletePendingAsync(chunk.Id, cancellationToken);
        window.Clear();
        if (!final) window.Add(chunks[^1]);
    }

    private async Task StopAcceptingAndDrainAsync(CancellationToken cancellationToken)
    {
        lock (_captureGate)
        {
            Volatile.Write(ref _accepting, false);
            RevokeActiveTimelineContextLocked();
        }
        if (_captureHandler is not null) _capture.SecondCaptured -= _captureHandler;
        if (_failureHandler is not null) _capture.CaptureFailed -= _failureHandler;
        await _capture.StopAsync();
        _ingestion?.Writer.TryComplete();
        if (_ingestionPump is not null) await _ingestionPump;
        while ((_pendingQueue.OutstandingCount > 0 || Volatile.Read(ref _inFlight) > 0) && !cancellationToken.IsCancellationRequested)
        {
            if (_consumerPump?.IsFaulted == true) await _consumerPump;
            await Task.Delay(25, cancellationToken);
        }
        await FlushWindowsAsync(cancellationToken);
        _consumerCts?.Cancel();
        if (_consumerPump is not null)
        {
            try { await _consumerPump; } catch (OperationCanceledException) { }
        }
        if (_diagnosticPump is not null)
        {
            try { await _diagnosticPump; } catch (OperationCanceledException) { }
        }
    }

    private async Task FlushWindowsAsync(CancellationToken cancellationToken)
    {
        foreach (var pair in _windows.ToArray())
            if (pair.Value.Count > 0) await ProcessWindowAsync(pair.Key, pair.Value, true, cancellationToken);
    }

    private async Task<WorkerResponse> TranscribeWithOneRestartAsync(string workId, byte[] pcm, CancellationToken cancellationToken)
    {
        try { return await _transport!.TranscribeAsync(workId, pcm, cancellationToken); }
        catch when (_workerRestartCount++ == 0)
        {
            await DisposeTransportAsync(CancellationToken.None);
            _transport = await _transportFactory.StartAsync(_modelPath!, _language, cancellationToken);
            return await _transport.TranscribeAsync(workId, pcm, cancellationToken);
        }
    }

    private async Task RollBackFailedStartAsync(CancellationToken cancellationToken)
    {
        lock (_captureGate)
        {
            Volatile.Write(ref _accepting, false);
            RevokeActiveTimelineContextLocked();
        }
        if (_captureHandler is not null) _capture.SecondCaptured -= _captureHandler;
        if (_failureHandler is not null) _capture.CaptureFailed -= _failureHandler;
        try { await _capture.StopAsync(); } catch { }
        _ingestion?.Writer.TryComplete();
        if (_ingestionPump is not null)
        {
            try { await _ingestionPump; } catch { }
        }
        _consumerCts?.Cancel();
        if (_consumerPump is not null)
        {
            try { await _consumerPump; } catch { }
        }
        if (_diagnosticPump is not null)
        {
            try { await _diagnosticPump; } catch { }
        }
        await DisposeTransportAsync(CancellationToken.None);
        if (_session is not null)
        {
            if (_audioArchive is not null) await _audioArchive.CompleteAsync(CancellationToken.None);
            var pending = await _store.GetPendingAsync(_session.Id, CancellationToken.None);
            var archived = await _store.GetArchivedAudioAsync(_session.Id, null, CancellationToken.None);
            if (pending.Count == 0 && archived.Count == 0) await _store.DeleteSessionAsync(_session.Id, CancellationToken.None);
            else await _store.CompleteSessionAsync(_session.Id, SessionState.Interrupted, DateTimeOffset.UtcNow, CancellationToken.None);
        }
        if (_audioArchive is not null) await _audioArchive.DisposeAsync();
        ResetAfterSession();
    }

    private async Task DisposeTransportAsync(CancellationToken cancellationToken)
    {
        if (_transport is null) return;
        try { await _transport.StopAsync(cancellationToken); } catch { }
        await _transport.DisposeAsync();
        _transport = null;
    }

    private async Task StopPumpsAfterFailureAsync()
    {
        _ingestion?.Writer.TryComplete();
        _consumerCts?.Cancel();
        if (_ingestionPump is not null)
        {
            try { await _ingestionPump; } catch { }
        }
        if (_consumerPump is not null)
        {
            try { await _consumerPump; } catch { }
        }
        if (_diagnosticPump is not null)
        {
            try { await _diagnosticPump; } catch { }
        }
    }

    private void ResetSessionState()
    {
        lock (_captureGate)
        {
            RevokeActiveTimelineContextLocked();
            _sessionTimeline = null;
        }
        _windows.Clear();
        _sequences.Clear();
        _drainFailed = false;
        _paused = false;
        _workerRestartCount = 0;
        _inFlight = 0;
        _audioBudgetBytes = 0;
        _diagnostics.Clear();
    }

    private void ResetAfterSession()
    {
        lock (_captureGate)
        {
            Volatile.Write(ref _accepting, false);
            RevokeActiveTimelineContextLocked();
            _sessionTimeline = null;
        }
        _session = null;
        _ingestion = null;
        _ingestionPump = null;
        _consumerPump = null;
        _diagnosticPump = null;
        _consumerCts?.Dispose();
        _consumerCts = null;
        _paused = false;
        if (_audioArchive is not null)
        {
            if (_archiveHandler is not null) _audioArchive.ChunkCommitted -= _archiveHandler;
            _audioArchive = null;
        }
        _captureHandler = null;
        _failureHandler = null;
        _archiveHandler = null;
    }

    private void InvalidateActiveTimeline(long revision, string sessionId)
    {
        lock (_captureGate)
        {
            var context = Volatile.Read(ref _activeTimelineContext);
            if (context is null ||
                context.Revision != revision ||
                !string.Equals(context.SessionId, sessionId, StringComparison.Ordinal))
                return;
            Volatile.Write(ref _accepting, false);
            _paused = true;
            RevokeActiveTimelineContextLocked();
        }
    }

    private void RevokeActiveTimelineContextLocked()
    {
        var context = Interlocked.Exchange(ref _activeTimelineContext, null);
        context?.Revoke();
    }

    private void OnArchiveChunkCommitted(long revision, ArchivedAudioChunk chunk)
    {
        if (revision != Volatile.Read(ref _revision)) return;
        var state = GetDiagnostic(chunk.Source);
        state.ArchivedChunks++;
        PublishDiagnostic(chunk.Source);
    }

    private SourceDiagnosticState GetDiagnostic(AudioSourceKind source)
    {
        return _diagnostics.GetOrAdd(source, static _ => new());
    }

    private void PublishDiagnostic(AudioSourceKind source, string? error = null)
    {
        var state = GetDiagnostic(source);
        if (error is not null) state.Error = error;
        var stalled = state.Error is not null || state.LastPcmAt is null || DateTimeOffset.UtcNow - state.LastPcmAt > TimeSpan.FromSeconds(5);
        DiagnosticChanged?.Invoke(this, new(source, state.LastPcmAt, state.CapturedSeconds,
            state.ArchivedChunks, state.PendingTranscription, state.Error, _audioArchive is not null, stalled));
    }

    private async Task PublishDiagnosticsAsync(long revision, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(cancellationToken))
            if (revision == Volatile.Read(ref _revision))
                foreach (var source in _diagnostics.Keys) PublishDiagnostic(source);
    }

    private sealed class SourceDiagnosticState
    {
        public DateTimeOffset? LastPcmAt { get; set; }
        public double CapturedSeconds { get; set; }
        public int ArchivedChunks { get; set; }
        public int PendingTranscription { get; set; }
        public string? Error { get; set; }
    }

    private readonly record struct CapturedAudioEnvelope(
        long Revision,
        string SessionId,
        TimeSpan Offset,
        CapturedSecond Captured);

    private static void ValidateSettings(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.ModelPath) || !File.Exists(settings.ModelPath))
            throw new InvalidOperationException("Selecciona un archivo de modelo Whisper GGML existente.");
        if (!settings.CaptureMicrophone && !settings.CaptureSystemOutput)
            throw new InvalidOperationException("Activa al menos una fuente de audio.");
    }

    public async ValueTask DisposeAsync()
    {
        try { await StopAsync(); } catch { }
        await DisposeTransportAsync(CancellationToken.None);
        _consumerCts?.Dispose();
        _lifecycle.Dispose();
    }
}
