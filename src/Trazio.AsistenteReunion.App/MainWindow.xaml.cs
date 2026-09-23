using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.App;

public partial class MainWindow : Window, IVisualCaptureStateSink
{
    private readonly AudioCaptureService _capture = new();
    private readonly IDataRootLocator _dataRootLocator;
    private readonly StorageMigrationService _storageMigration;
    private readonly SettingsStore _settingsStore = new(ApplicationPaths.SettingsPath);
    private readonly ObservableCollection<TranscriptRow> _liveRows = [];
    private readonly ObservableCollection<HistorySegmentItem> _historyRows = [];
    private readonly ObservableCollection<TranscriptComparisonRow> _comparisonRows = [];
    private bool _settingComparisonSelectors;
    private bool _comparisonLoading;
    private readonly ObservableCollection<GlossarySuggestionItem> _glossarySuggestions = [];
    private readonly ObservableCollection<double> _waveformBars = [];
    private IReadOnlyList<AudioArchiveSummary> _historyAudio = [];
    private IReadOnlyList<ArchivedAudioChunk> _historyTrackChunks = [];
    private TimeSpan _historyTrackDuration;
    private TimeSpan _playbackBasePosition;
    private AudioSourceKind _historyTrackSource = AudioSourceKind.Microphone;
    private string? _historyTrackSessionId;
    private readonly DispatcherTimer _playbackTimer;
    private readonly IMeetingWindowCatalog _meetingWindowCatalog;
    private readonly MeetingWindowSelectionController _meetingWindowSelection;
    private readonly DispatcherTimer _meetingWindowMonitorTimer;
    private readonly VisualCaptureStateTracker _visualCaptureStateTracker = new();
    private readonly SemaphoreSlim _visualCaptureActions = new(1, 1);
    private VisualCaptureAuthorization? _visualCaptureAuthorization;
    private VisualCaptureSessionController? _visualCaptureController;
    private VisualCaptureState _visualCaptureState = VisualCaptureState.Off;
    private bool _visualCaptureActionInProgress;
    private bool _visualPausedByRecording;
    private bool _historySelectedSourceAudioComplete;
    private bool _suppressHistorySegmentPlayback;
    private bool _historyPlaybackPaused;
    private readonly HistorySelectionCoordinator _historyLoads = new();
    private readonly RevisionSelectionCoordinator _historyRevisionLoads = new();
    private readonly PlaybackOperationCoordinator _playbackOperations = new();
    private AesContentProtector? _protector;
    private SqliteSessionStore? _store;
    private AudioArchiveStore? _audioArchive;
    private HistoryRetranscriptionService? _retranscription;
    private readonly OwnedCancellationOperationCoordinator _historyRetranscriptionOperation = new();
    private bool _settingHistoryRevision;
    private readonly AudioPlaybackService _playback = new();
    private RecordingCoordinator? _coordinator;
    private AppSettings _settings = new();
    private bool _recording;
    private bool _recordingPaused;
    private bool _closingConfirmed;
    private bool _closing;
    private bool _busy;
    private bool _initialized;
    private bool _historyPlaying;
    private bool _settingHistorySource;
    private HistoryViewState _historyState = HistoryPresenter.Create(false, null, [], [], AudioSourceKind.Microphone, _ => string.Empty);
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _downloadCancellation;
    private Task _operation = Task.CompletedTask;
    private static readonly HttpClient ModelHttpClient = new() { Timeout = TimeSpan.FromMinutes(30) };
    private readonly WhisperModelStore _models = new(ModelHttpClient, ApplicationPaths.ModelsDirectory, WhisperModelDefinition.Recommended);

    public MainWindow(IDataRootLocator dataRootLocator, StorageMigrationService storageMigration)
    {
        _dataRootLocator = dataRootLocator;
        _storageMigration = storageMigration;
        InitializeComponent();
        LiveTranscript.ItemsSource = _liveRows;
        HistorySegments.ItemsSource = _historyRows;
        ComparisonRows.ItemsSource = _comparisonRows;
        GlossarySuggestionsList.ItemsSource = _glossarySuggestions;
        AudioWaveformItems.ItemsSource = _waveformBars;
        _playbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _playbackTimer.Tick += PlaybackTimer_Tick;
        _meetingWindowCatalog = new Win32MeetingWindowCatalog();
        _meetingWindowSelection = new MeetingWindowSelectionController(_meetingWindowCatalog);
        _meetingWindowMonitorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _meetingWindowMonitorTimer.Tick += MeetingWindowMonitorTimer_Tick;
        Loaded += OnLoaded;
        SetRecordingButtons(false, false);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e) => await RunExclusiveAsync(InitializeAsync);

    private async Task InitializeAsync()
    {
        Directory.CreateDirectory(ApplicationPaths.DataDirectory);
        _protector = new(MasterKeyStore.LoadOrCreate(ApplicationPaths.KeyPath));
        _store = new(ApplicationPaths.DatabasePath, _protector);
        await _store.InitializeAsync();
        var recoverableSessions = await new StartupRecoveryService(_store).PrepareAsync(DateTimeOffset.UtcNow);
        _audioArchive = new(ApplicationPaths.AudioDirectory, _store, _protector);
        _retranscription = new(_store, _audioArchive, new ProcessTranscriptionTransportFactory());
        await _audioArchive.ReconcileAsync(_lifetime.Token);
        _settings = AudioRetentionPolicy.Enforce(
            LocalProfile.EnsureDefault(await _settingsStore.LoadAsync(_lifetime.Token), Environment.UserName));
        await _settingsStore.SaveAsync(_settings, _lifetime.Token);
        LocalDisplayNameBox.Text = _settings.LocalDisplayName ?? string.Empty;
        LocalOrganizationBox.Text = _settings.LocalOrganization ?? string.Empty;
        ConfirmLocalProfileCheck.IsChecked = _settings.LocalProfileConfirmed;
        TitleBox.Text = DefaultSessionTitle();
        var storage = StorageLocationFacts.Current();
        StoragePathText.Text = storage.DataDirectory;
        StorageExplanationText.Text = storage.Explanation;
        UpdateStorageLocationUi();
        KeepAudioCheck.IsChecked = AudioRetentionPolicy.Required;
        SelectAudioBudget(_settings.AudioStorageBudgetGb);
        await _audioArchive.PruneAsync(_settings.AudioStorageBudgetGb * 1024L * 1024 * 1024, _lifetime.Token);
        LoadDevices();
        ModelPathBox.Text = _settings.ModelPath ?? string.Empty;
        SelectLanguage(_settings.Language);
        try
        {
            var model = await _models.ResolveAsync(_settings.ModelPath, Path.Combine(AppContext.BaseDirectory, "models"), _lifetime.Token);
            if (model is not null) await SaveModelAsync(model);
            else ModelStatus.Text = "Whisper Base multilingüe · 148 MB. Se descarga una vez y la transcripción se mantiene en este equipo.";
        }
        catch (InvalidDataException ex) { ModelStatus.Text = ex.Message; }
        await RefreshHistoryAsync();
        _initialized = true;
        RefreshHistoryButton.IsEnabled = true;
        UpdateStorageControls();
        UpdateVisualCaptureUi();
        await OfferPendingRecoveryAsync(recoverableSessions);
    }

    private async Task RunExclusiveAsync(Func<Task> action)
    {
        if (_busy || _closing) return;
        _busy = true;
        SetRecordingButtons(_recording, _recordingPaused);
        _operation = ExecuteAsync();
        await _operation;
        async Task ExecuteAsync()
        {
            try { await action(); }
            catch (OperationCanceledException) { ModelStatus.Text = "Configuración del modelo cancelada. Puedes intentarlo nuevamente."; }
            catch (Exception ex) { if (!_closing) ShowError("No se pudo completar la operación", ex.Message); }
            finally { _busy = false; SetRecordingButtons(_recording, _recordingPaused); }
        }
    }

    private async Task SaveModelAsync(string path)
    {
        ModelPathBox.Text = path;
        _settings = _settings with { ModelPath = path };
        await _settingsStore.SaveAsync(_settings, _lifetime.Token);
        ModelStatus.Text = "Modelo listo · la transcripción se ejecuta localmente.";
    }

    private async Task EnsureModelAsync()
    {
        var path = await _models.ResolveAsync(ModelPathBox.Text.Trim(), Path.Combine(AppContext.BaseDirectory, "models"), _lifetime.Token);
        if (path is null)
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _downloadCancellation = cancellation;
            CancelModelButton.IsEnabled = true;
            ModelStatus.Text = "Descargando Whisper Base multilingüe (148 MB)…";
            var progress = new Progress<ModelDownloadProgress>(p =>
            {
                if (_downloadCancellation is null) return;
                ModelProgress.Value = 100.0 * p.ReceivedBytes / p.TotalBytes;
                ModelStatus.Text = $"Descargando modelo: {p.ReceivedBytes / 1_000_000.0:F1} / {p.TotalBytes / 1_000_000.0:F1} MB";
            });
            try { path = await _models.DownloadAsync(progress, cancellation.Token); }
            finally { _downloadCancellation = null; CancelModelButton.IsEnabled = false; }
        }
        _lifetime.Token.ThrowIfCancellationRequested();
        await SaveModelAsync(path);
    }

    private async void SaveLocalProfile_Click(object sender, RoutedEventArgs e) =>
        await RunExclusiveAsync(SaveLocalProfileAsync);

    private async Task SaveLocalProfileAsync()
    {
        try
        {
            _settings = LocalProfile.SaveConfirmedProfile(
                _settings,
                LocalDisplayNameBox.Text,
                LocalOrganizationBox.Text,
                ConfirmLocalProfileCheck.IsChecked == true);
            await _settingsStore.SaveAsync(_settings, _lifetime.Token);
            LocalDisplayNameBox.Text = _settings.LocalDisplayName ?? string.Empty;
            LocalOrganizationBox.Text = _settings.LocalOrganization ?? string.Empty;
            ConfirmLocalProfileCheck.IsChecked = true;
            StatusText.Text = "Perfil local guardado";
        }
        catch (Exception ex)
        {
            ShowError("No se pudo guardar el perfil local", ex.Message);
        }
    }
    private async void DownloadModel_Click(object sender, RoutedEventArgs e) => await RunExclusiveAsync(EnsureModelAsync);
    private void CancelModel_Click(object sender, RoutedEventArgs e) => _downloadCancellation?.Cancel();

    private void LoadDevices()
    {
        MicrophoneBox.ItemsSource = AudioCaptureService.GetMicrophones();
        OutputBox.ItemsSource = AudioCaptureService.GetOutputs();
        MicrophoneBox.SelectedValue = _settings.MicrophoneDeviceId;
        OutputBox.SelectedValue = _settings.OutputDeviceId;
        MicrophoneCheck.IsChecked = _settings.CaptureMicrophone;
        OutputCheck.IsChecked = _settings.CaptureSystemOutput;
        if (MicrophoneBox.SelectedItem is null && !string.IsNullOrEmpty(_settings.MicrophoneDeviceId)) StatusText.Text = "El micrófono guardado no está disponible. Selecciónalo nuevamente.";
        if (OutputBox.SelectedItem is null && !string.IsNullOrEmpty(_settings.OutputDeviceId)) StatusText.Text = "El dispositivo de salida guardado no está disponible. Selecciónalo nuevamente.";
    }

    private void SelectMeetingWindow_Click(object sender, RoutedEventArgs e)
    {
        if (_recording || _busy || _closing) return;
        var dialog = new MeetingWindowPickerWindow(_meetingWindowCatalog) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SelectedSelection is null) return;
        InvalidateVisualAuthorization();
        _meetingWindowSelection.Select(dialog.SelectedSelection);
        SetVisualCaptureState(VisualCaptureState.Off);
        UpdateMeetingWindowUi();
        StatusText.Text = $"{MeetingProviderPresentation.Name(dialog.SelectedSelection.Provider)} seleccionado para la próxima sesión";
    }

    private void ClearMeetingWindow_Click(object sender, RoutedEventArgs e)
    {
        if (_recording || _busy || _closing) return;
        InvalidateVisualAuthorization();
        _meetingWindowSelection.Clear();
        SetVisualCaptureState(VisualCaptureState.Off);
        UpdateMeetingWindowUi();
        StatusText.Text = "Asociación de aplicación de reunión eliminada";
    }

    private async void AuthorizeVisualCapture_Click(object sender, RoutedEventArgs e)
    {
        if (_visualCaptureActionInProgress || _busy || _closing) return;
        if (_recordingPaused)
        {
            StatusText.Text = "Reanuda la reunión antes de autorizar el análisis visual.";
            UpdateVisualCaptureUi();
            return;
        }
        var selection = _meetingWindowSelection.Selection;
        if (!IsCurrentVisualSelectionAvailable(selection))
        {
            InvalidateVisualAuthorization();
            UpdateVisualCaptureUi();
            StatusText.Text = "Selecciona una ventana disponible antes de autorizar el análisis visual.";
            return;
        }

        var dialog = new VisualCaptureConsentWindow { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            AuthorizeVisualCaptureButton.Focus();
            StatusText.Text = "No se activó el análisis visual. La transcripción continuará sin cambios.";
            return;
        }

        if (_meetingWindowSelection.Selection != selection || !IsCurrentVisualSelectionAvailable(selection))
        {
            InvalidateVisualAuthorization();
            UpdateVisualCaptureUi();
            StatusText.Text = "La ventana seleccionada cambió o dejó de estar disponible. Autoriza nuevamente.";
            return;
        }

        _visualCaptureAuthorization = VisualCaptureAuthorization.GrantForSelection(selection!);
        SetVisualCaptureState(VisualCaptureState.Off);
        UpdateVisualCaptureUi();

        if (_recording)
        {
            await RunVisualCaptureActionAsync(() => StartAuthorizedVisualCaptureAsync(selection!));
        }
        VisualCaptureStatusText.Focus();
    }

    private async void Start_Click(object sender, RoutedEventArgs e) => await RunExclusiveAsync(StartSessionAsync);

    private async Task StartSessionAsync()
    {
        RecordingCoordinator? candidate = null;
        MeetingWindowSelection? visualSelection = null;
        var audioStarted = false;
        try
        {
            await EnsureModelAsync();
            _lifetime.Token.ThrowIfCancellationRequested();
            _settings = AudioRetentionPolicy.Enforce(ReadSettings());
            await _settingsStore.SaveAsync(_settings);
            var hadSelectedWindow = _meetingWindowSelection.Selection is not null;
            var meetingProvider = _meetingWindowSelection.ResolveProviderForStart();
            var selectionLostAtStart = hadSelectedWindow && meetingProvider == MeetingProvider.NotSelected;
            visualSelection = _meetingWindowSelection.Selection;
            if (_visualCaptureAuthorization is not null &&
                !_visualCaptureAuthorization.Matches(visualSelection))
                InvalidateVisualAuthorization();
            UpdateMeetingWindowUi();
            if (_coordinator is not null) { await _coordinator.DisposeAsync(); _coordinator = null; }
            candidate = CreateCoordinator();
            await candidate.StartAsync(TitleBox.Text, _settings, _lifetime.Token,
                localDisplayNameOverride: MeetingDisplayNameOverrideBox.Text,
                meetingProvider: meetingProvider);
            MeetingDisplayNameOverrideBox.Clear();
            _coordinator = candidate;
            _recording = true;
            _recordingPaused = false;
            audioStarted = true;
            if (_meetingWindowSelection.Selection is not null) _meetingWindowMonitorTimer.Start();
            SetRecordingButtons(true, false);
            if (selectionLostAtStart)
                StatusText.Text = "La ventana seleccionada ya no está disponible. La transcripción continúa sin asociar un proveedor.";
        }
        catch (Exception ex)
        {
            InvalidateVisualAuthorization();
            _meetingWindowMonitorTimer.Stop();
            _meetingWindowSelection.FinishSession();
            if (candidate is not null) await candidate.DisposeAsync();
            _coordinator = null;
            _recording = false;
            _recordingPaused = false;
            SetRecordingButtons(false, false);
            if (ex is OperationCanceledException) throw;
            if (!_closing) ShowError("No se pudo iniciar", ex.Message);
        }

        if (audioStarted && visualSelection is not null)
            await RunVisualCaptureActionAsync(() => StartAuthorizedVisualCaptureAsync(visualSelection));
    }

    private async void Pause_Click(object sender, RoutedEventArgs e)
    {
        _coordinator?.Pause();
        _recordingPaused = true;
        SetRecordingButtons(true, true);
        if (_visualCaptureController?.State is VisualCaptureState.Active or VisualCaptureState.Starting)
        {
            _visualPausedByRecording = true;
            await RunVisualCaptureActionAsync(async () =>
            {
                if (_visualCaptureController is not null) await _visualCaptureController.PauseAsync();
            });
        }
    }

    private async void Resume_Click(object sender, RoutedEventArgs e)
    {
        _coordinator?.Resume();
        _recordingPaused = false;
        SetRecordingButtons(true, false);
        var pendingSelection = _meetingWindowSelection.Selection;
        if (_visualCaptureAuthorization?.Matches(pendingSelection) == true && pendingSelection is not null)
        {
            await RunVisualCaptureActionAsync(() => StartAuthorizedVisualCaptureAsync(pendingSelection));
            return;
        }
        if (!_visualPausedByRecording) return;
        _visualPausedByRecording = false;
        await RunVisualCaptureActionAsync(async () =>
        {
            if (_visualCaptureController is not null) await _visualCaptureController.ResumeAsync(_lifetime.Token);
        });
    }

    private async void PauseVisualCapture_Click(object sender, RoutedEventArgs e)
    {
        _visualPausedByRecording = false;
        await RunVisualCaptureActionAsync(async () =>
        {
            if (_visualCaptureController is not null) await _visualCaptureController.PauseAsync();
        });
    }

    private async void ResumeVisualCapture_Click(object sender, RoutedEventArgs e)
    {
        _visualPausedByRecording = false;
        await RunVisualCaptureActionAsync(async () =>
        {
            if (_visualCaptureController is not null) await _visualCaptureController.ResumeAsync(_lifetime.Token);
        });
    }

    private async void StopVisualCapture_Click(object sender, RoutedEventArgs e)
    {
        _visualPausedByRecording = false;
        await RunVisualCaptureActionAsync(() => StopAndDisposeVisualCaptureAsync(VisualCaptureState.Stopped));
    }

    private async void Stop_Click(object sender, RoutedEventArgs e) => await RunExclusiveAsync(StopSessionAsync);

    private async Task StopSessionAsync()
    {
        try
        {
            await VisualCaptureShutdown.RunVisualFirstAsync(
                () => RunVisualCaptureActionAsync(() => StopAndDisposeVisualCaptureAsync(VisualCaptureState.Off)),
                () => _coordinator?.StopAsync() ?? Task.CompletedTask);
        }
        catch (Exception ex) { ShowError("No se pudo detener correctamente", ex.Message); }
        finally
        {
            _meetingWindowMonitorTimer.Stop();
            _recording = false;
            _recordingPaused = false;
            _meetingWindowSelection.FinishSession();
            InvalidateVisualAuthorization();
            SetRecordingButtons(false, false);
            if (_coordinator is not null) { await _coordinator.DisposeAsync(); _coordinator = null; }
            TitleBox.Text = DefaultSessionTitle();
            await RefreshHistoryAsync();
        }
    }

    private void SetRecordingButtons(bool recording, bool paused)
    {
        _recordingPaused = recording && paused;
        var ready = _initialized && !_busy && !_closing;
        StartButton.IsEnabled = !recording && ready;
        StopButton.IsEnabled = recording && !_busy && !_closing;
        PauseButton.IsEnabled = recording && !paused && !_busy && !_closing;
        ResumeButton.IsEnabled = recording && paused && !_busy && !_closing;
        MicrophoneBox.IsEnabled = OutputBox.IsEnabled = ModelPathBox.IsEnabled = !recording && ready;
        BrowseModelButton.IsEnabled = DownloadModelButton.IsEnabled = !recording && ready;
        MicrophoneCheck.IsEnabled = OutputCheck.IsEnabled = LanguageBox.IsEnabled = !recording && ready;
        KeepAudioCheck.IsChecked = AudioRetentionPolicy.Required;
        KeepAudioCheck.IsEnabled = false;
        AudioBudgetBox.IsEnabled = !recording && ready;
        LocalDisplayNameBox.IsEnabled = LocalOrganizationBox.IsEnabled = ConfirmLocalProfileCheck.IsEnabled = !recording && ready;
        SaveLocalProfileButton.IsEnabled = !recording && ready;
        TitleBox.IsEnabled = !recording && ready;
        MeetingDisplayNameOverrideBox.IsEnabled = !recording && ready;
        RecordingAudioIndicator.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
        UpdateMeetingWindowUi();
        UpdateVisualCaptureUi();
        UpdateStorageControls();
    }

    private void MeetingWindowMonitorTimer_Tick(object? sender, EventArgs e)
    {
        if (!_recording)
        {
            _meetingWindowMonitorTimer.Stop();
            return;
        }
        if (_meetingWindowSelection.CheckWhileRecording() != MeetingWindowAvailability.Lost) return;
        _meetingWindowMonitorTimer.Stop();
        UpdateMeetingWindowUi();
        StatusText.Text = "La ventana asociada se cerró o cambió. La captura de audio y la transcripción continúan sin reasignarla.";
    }

    private void UpdateMeetingWindowUi()
    {
        if (!IsInitialized) return;
        var state = MeetingWindowSelectionPresenter.Create(
            _meetingWindowSelection.Selection,
            _meetingWindowSelection.IsLost,
            _recording,
            _initialized && !_busy && !_closing);
        MeetingWindowStatusText.Text = state.Status;
        SelectMeetingWindowButton.Content = state.SelectButtonText;
        SelectMeetingWindowButton.IsEnabled = state.CanSelect;
        ClearMeetingWindowButton.IsEnabled = state.CanClear;
        UpdateVisualCaptureUi();
    }

    private async Task RunVisualCaptureActionAsync(Func<Task> action)
    {
        await _visualCaptureActions.WaitAsync();
        try
        {
            _visualCaptureActionInProgress = true;
            UpdateVisualCaptureUi();
            await action();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            await StopAndDisposeVisualCaptureAsync(VisualCaptureState.Stopped);
        }
        catch
        {
            await StopAndDisposeVisualCaptureAsync(VisualCaptureState.Failed);
            if (!_closing)
                StatusText.Text = "El análisis visual no pudo continuar. El audio y la transcripción siguen activos.";
        }
        finally
        {
            _visualCaptureActionInProgress = false;
            UpdateVisualCaptureUi();
            _visualCaptureActions.Release();
        }
    }

    private async Task StartAuthorizedVisualCaptureAsync(MeetingWindowSelection selection)
    {
        if (!VisualCaptureActivationPolicy.CanStart(_recording, _recordingPaused, _closing) ||
            _visualCaptureController is not null) return;
        var authorization = _visualCaptureAuthorization;
        if (authorization is null) return;

        var sessionIdText = _coordinator?.ActiveSessionId;
        if (!Guid.TryParseExact(sessionIdText, "N", out var sessionId) ||
            _meetingWindowSelection.Selection != selection ||
            !IsCurrentVisualSelectionAvailable(selection) ||
            !authorization.TryConsume(selection, sessionId, out var consent) ||
            consent is null)
        {
            InvalidateVisualAuthorization();
            SetVisualCaptureState(VisualCaptureState.Off);
            StatusText.Text = "No se activó el análisis visual porque la sesión o la ventana seleccionada cambió.";
            return;
        }

        _visualCaptureAuthorization = null;
        var observer = new WeakVisualCaptureStateObserver<MainWindow>(this);
        var controller = new VisualCaptureSessionController(
            sessionId,
            new WindowsGraphicsCaptureService(selection),
            observer);
        _visualCaptureController = controller;
        _visualCaptureStateTracker.Reset(controller.Identity);
        SetVisualCaptureState(VisualCaptureState.Off);

        _ = await controller.StartAsync(consent, _lifetime.Token);
    }

    private async Task StopAndDisposeVisualCaptureAsync(VisualCaptureState finalState)
    {
        InvalidateVisualAuthorization();
        _visualPausedByRecording = false;
        var controller = _visualCaptureController;
        _visualCaptureController = null;
        _visualCaptureStateTracker.Reset(Guid.Empty);
        if (controller is not null)
        {
            try
            {
                await controller.StopAsync();
                await controller.DisposeAsync();
            }
            catch
            {
                // Visual cleanup is isolated from audio, transcription, and application shutdown.
            }
        }
        SetVisualCaptureState(finalState);
    }

    private void OnVisualCaptureStateChanged(VisualCaptureStateChange change)
    {
        if (_closing || Dispatcher.HasShutdownStarted) return;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(() =>
        {
            if (_closing || !_visualCaptureStateTracker.TryAccept(change)) return;
            SetVisualCaptureState(change.State);
        }));
    }

    void IVisualCaptureStateSink.ReceiveVisualCaptureState(VisualCaptureStateChange change) =>
        OnVisualCaptureStateChanged(change);

    private bool IsCurrentVisualSelectionAvailable(MeetingWindowSelection? selection)
    {
        if (selection is null || _meetingWindowSelection.IsLost ||
            _meetingWindowSelection.Selection != selection) return false;
        try
        {
            return _meetingWindowCatalog.IsAvailable(selection);
        }
        catch
        {
            return false;
        }
    }

    private void InvalidateVisualAuthorization() => _visualCaptureAuthorization = null;

    private void SetVisualCaptureState(VisualCaptureState state)
    {
        _visualCaptureState = state;
        UpdateVisualCaptureUi();
    }

    private void UpdateVisualCaptureUi()
    {
        if (!IsInitialized) return;
        var selection = _meetingWindowSelection.Selection;
        var hasValidSelection = selection is not null && !_meetingWindowSelection.IsLost;
        var hasAuthorization = _visualCaptureAuthorization?.Matches(selection) == true;
        var state = VisualCapturePresenter.Create(
            _visualCaptureState,
            hasValidSelection,
            hasAuthorization,
            _initialized && !_busy && !_closing,
            _recordingPaused,
            _visualCaptureActionInProgress);

        if (!string.Equals(VisualCaptureStatusText.Text, state.Status, StringComparison.Ordinal))
            VisualCaptureStatusText.Text = state.Status;
        AuthorizeVisualCaptureButton.IsEnabled = state.CanAuthorize;
        PauseVisualCaptureButton.IsEnabled = state.CanPause;
        ResumeVisualCaptureButton.IsEnabled = state.CanResume;
        StopVisualCaptureButton.IsEnabled = state.CanStop;
        StopVisualCaptureButton.Visibility = state.ShowStop ? Visibility.Visible : Visibility.Collapsed;
        VisualCaptureActiveIndicator.Visibility = state.ShowActiveIndicator
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void BrowseModel_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Seleccionar un modelo Whisper GGML", Filter = "Modelo Whisper (*.bin)|*.bin|Todos los archivos (*.*)|*.*" };
        if (dialog.ShowDialog(this) == true) ModelPathBox.Text = dialog.FileName;
    }

    private async void HistoryTab_GotFocus(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, sender)) return;
        if (!_initialized) { StatusText.Text = "El historial todavía se está cargando"; return; }
        try { await RefreshHistoryAsync(); }
        catch (Exception ex) { ShowError("No se pudieron actualizar las sesiones", ex.Message); }
    }

    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || !ReferenceEquals(e.OriginalSource, MainTabs) || ReferenceEquals(MainTabs.SelectedItem, HistoryTabItem)) return;
        if (!_historyPlaying) return;
        SupersedePlayback();
        StatusText.Text = "La reproducción se detuvo porque se cerró el Historial";
    }

    private async void RefreshHistory_Click(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _store is null) { StatusText.Text = "El historial todavía se está cargando"; return; }
        try
        {
            await RefreshHistoryAsync();
            StatusText.Text = "Sesiones guardadas actualizadas";
        }
        catch (Exception ex) { ShowError("No se pudieron actualizar las sesiones", ex.Message); }
    }

    private async Task RefreshHistoryAsync()
    {
        if (_store is null) throw new InvalidOperationException("El almacenamiento del historial todavía no está listo.");
        var selectedId = SelectedHistorySession()?.Id;
        var sessions = (await _store.ListSessionsAsync())
            .Select(session => HistorySessionItem.From(session))
            .ToArray();
        HistoryList.ItemsSource = sessions;
        HistoryList.SelectedItem = sessions.FirstOrDefault(item => item.Session.Id == selectedId);
        if (HistoryList.SelectedItem is null) ApplyHistoryState(HistoryPresenter.Create(false, null, [], [], SelectedHistorySource(), FormatTranscript));
    }

    private async Task OfferPendingRecoveryAsync(IReadOnlyList<RecoverableSession> recoverableSessions)
    {
        if (_store is null) return;
        foreach (var recoverable in recoverableSessions)
        {
            var session = recoverable.Session;
            var pending = recoverable.PendingAudio;
            if (MessageBox.Show(this, $"La sesión interrumpida '{session.Title}' tiene {pending.Count} fragmentos de audio cifrado pendientes de transcripción. ¿Deseas recuperarlos ahora?", "Recuperar sesión interrumpida", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) continue;
            try
            {
                await EnsureModelAsync();
                _lifetime.Token.ThrowIfCancellationRequested();
                _coordinator = CreateCoordinator();
                await _coordinator.RecoverAsync(session, _settings, pending, _lifetime.Token);
                await _coordinator.DisposeAsync();
                _coordinator = null;
                await RefreshHistoryAsync();
            }
            catch (Exception ex)
            {
                if (_coordinator is not null) await _coordinator.DisposeAsync();
                _coordinator = null;
                ShowError("No se pudo completar la recuperación", ex.Message);
            }
            break;
        }
    }

    private RecordingCoordinator CreateCoordinator()
    {
        var coordinator = new RecordingCoordinator(_capture, _store!, new PendingAudioQueue(), audioArchiveStore: _audioArchive);
        coordinator.StatusChanged += (_, status) => Dispatcher.Invoke(() => StatusText.Text = status);
        coordinator.LevelChanged += (_, level) => Dispatcher.Invoke(() =>
        {
            if (level.Source == AudioSourceKind.Microphone) MicrophoneLevel.Value = level.Peak;
            else OutputLevel.Value = level.Peak;
        });
        coordinator.SegmentReady += (_, segment) => Dispatcher.Invoke(() =>
        {
            _liveRows.Add(new($"{segment.Start:hh\\:mm\\:ss} · {TranscriptPresentation.SpeakerLabel(segment)}", segment.Text));
            LiveTranscript.ScrollIntoView(_liveRows.Last());
        });
        coordinator.DiagnosticChanged += (_, diagnostic) => Dispatcher.Invoke(() =>
        {
            var age = diagnostic.LastPcmAt is null ? "sin audio PCM recibido" : $"último PCM hace {(DateTimeOffset.UtcNow - diagnostic.LastPcmAt.Value).TotalSeconds:F0} s";
            var archive = diagnostic.AudioArchiveEnabled ? $"{diagnostic.ArchivedChunks} fragmentos cifrados" : "Audio desactivado";
            var error = string.IsNullOrWhiteSpace(diagnostic.LatestError) ? string.Empty : $" · ERROR: {diagnostic.LatestError}";
            var text = $"{SourceName(diagnostic.Source)}: {age} · {diagnostic.CapturedSeconds:F0} s capturados · {archive} · {diagnostic.PendingTranscription} pendientes{error}";
            var color = diagnostic.IsStalled ? System.Windows.Media.Brushes.DarkOrange : System.Windows.Media.Brushes.DarkGreen;
            if (diagnostic.Source == AudioSourceKind.Microphone) { MicrophoneDiagnostic.Text = text; MicrophoneDiagnostic.Foreground = color; }
            else { OutputDiagnostic.Text = text; OutputDiagnostic.Foreground = color; }
        });
        return coordinator;
    }

private async void HistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        CancelHistoryRetranscription();
        _historyRevisionLoads.Invalidate();
        SupersedePlayback();
        PopulateComparisonSelectors([]);
        var session = SelectedHistorySession();
        UpdateSessionTitleEditor(session);
        if (session is null || _store is null)
        {
            _historyLoads.Invalidate();
            ClearHistoryReview();
            ApplyHistoryState(HistoryPresenter.Create(false, null, [], [], SelectedHistorySource(), FormatTranscript));
            return;
        }

        var ticket = _historyLoads.Begin(session.Id);
        try { await LoadHistoryReviewAsync(session, ticket); }
        catch (OperationCanceledException) when (!_historyLoads.IsCurrent(ticket, SelectedHistorySession()?.Id)) { }
        catch (Exception ex) { if (_historyLoads.IsCurrent(ticket, SelectedHistorySession()?.Id)) ShowError("No se pudo cargar la sesión", ex.Message); }
    }

    private void UpdateSessionTitleEditor(SessionSummary? session)
    {
        HistoryTitleBox.Text = session?.Title ?? string.Empty;
        HistoryTitleBox.IsEnabled = session is not null;
        SaveSessionTitleButton.IsEnabled = session is not null && !_busy;
        HistoryMeetingProviderText.Text = $"Aplicación de reunión: {MeetingProviderPresentation.Name(session?.MeetingProvider ?? MeetingProvider.NotSelected)}";
    }

    private async void SaveSessionTitle_Click(object sender, RoutedEventArgs e)
    {
        await RunExclusiveAsync(SaveSessionTitleAsync);
        UpdateSessionTitleEditor(SelectedHistorySession());
    }

    private async Task SaveSessionTitleAsync()
    {
        var session = SelectedHistorySession();
        if (session is null || _store is null) return;
        var title = HistoryTitleBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            StatusText.Text = "Escribe un título antes de guardarlo.";
            return;
        }
        if (string.Equals(title, session.Title, StringComparison.Ordinal))
        {
            StatusText.Text = "El título no tiene cambios.";
            return;
        }
        if (!await _store.UpdateSessionTitleAsync(session.Id, title, _lifetime.Token))
            throw new InvalidOperationException("La sesión ya no existe en el historial.");
        await RefreshHistoryAsync();
        StatusText.Text = "Título de la reunión actualizado.";
    }

    private async Task LoadHistoryReviewAsync(
        SessionSummary session,
        HistoryLoadTicket ticket,
        string? selectedSegmentId = null,
        RevisionSelectionTicket? revisionTicket = null,
        bool refreshRevisionSelector = true)
    {
        if (_store is null) return;
        var selectedSource = SelectedHistorySource();
        var reviewed = await _store.GetReviewedSegmentsAsync(session.Id, ticket.CancellationToken);
        var audio = await _store.GetAudioArchiveSummaryAsync(session.Id, ticket.CancellationToken);
        var revisions = await _store.ListModelRevisionsAsync(
            session.Id,
            selectedSource,
            successfulOnly: true,
            ticket.CancellationToken);
        var selectedSourceChunks = await _store.GetArchivedAudioAsync(
            session.Id,
            selectedSource,
            ticket.CancellationToken);
        if (!_historyLoads.IsCurrent(ticket, SelectedHistorySession()?.Id)) return;
        if (revisionTicket is not null && !IsCurrentRevisionSelection(revisionTicket)) return;
        IReadOnlyList<double> waveform = _audioArchive is null
            ? []
            : await AudioWaveformBuilder.BuildAsync(
                _audioArchive,
                session.StartedAt,
                selectedSourceChunks,
                cancellationToken: ticket.CancellationToken);
        if (!_historyLoads.IsCurrent(ticket, SelectedHistorySession()?.Id)) return;
        if (revisionTicket is not null && !IsCurrentRevisionSelection(revisionTicket)) return;
        ApplyHistoryTrack(session, selectedSource, selectedSourceChunks, waveform);
        _historyAudio = audio;
        _historySelectedSourceAudioComplete = IsCompleteRetainedSource(selectedSourceChunks, session.StartedAt);
        if (refreshRevisionSelector) PopulateHistoryRevisionSelector(revisions);
        HistoryAudioSummary.Text = audio.Count == 0 ? "No hay audio conservado" : string.Join(" · ", audio.Select(a =>
            $"{SourceName(a.Source)}: {a.ChunkCount} fragmentos, {a.Duration:hh\\:mm\\:ss}, {a.EncryptedBytes / 1_000_000.0:F1} MB"));
        _suppressHistorySegmentPlayback = true;
        try
        {
            _historyRows.Clear();
            foreach (var item in reviewed) _historyRows.Add(new(item));
            HistorySegments.SelectedItem = _historyRows.FirstOrDefault(item => item.Segment.Id == selectedSegmentId);
        }
        finally { _suppressHistorySegmentPlayback = false; }
        HistoryEmptyText.Visibility = reviewed.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyHistoryState(HistoryPresenter.Create(true, session.State, reviewed.Select(item => item.Segment).ToArray(), audio,
            selectedSource, _ => TranscriptPresentation.FormatReviewed(reviewed)));
        UpdateSelectedSegmentEditor();
    }

    private async void HistoryAudioSource_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settingHistorySource || !_initialized) return;
        CancelHistoryRetranscription();
        _historyRevisionLoads.Invalidate();
        SupersedePlayback();
        PopulateComparisonSelectors([]);
        var session = SelectedHistorySession();
        if (session is null || _store is null)
        {
            _historyLoads.Invalidate();
            ApplyHistoryState(HistoryPresenter.Create(false, null, [], [], SelectedHistorySource(), FormatTranscript));
            return;
        }
        var selectedSegmentId = SelectedHistorySegment()?.Segment.Id;
        var ticket = _historyLoads.Begin(session.Id);
        try { await LoadHistoryReviewAsync(session, ticket, selectedSegmentId); }
        catch (OperationCanceledException) when (!_historyLoads.IsCurrent(ticket, SelectedHistorySession()?.Id)) { }
        catch (Exception ex) { if (_historyLoads.IsCurrent(ticket, SelectedHistorySession()?.Id)) ShowError("No se pudo cambiar la fuente de audio", ex.Message); }
    }

    private void HistorySegments_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSelectedSegmentEditor();
        var selected = SelectedHistorySegment();
        if (_suppressHistorySegmentPlayback || selected is null || _historyPlaying) return;
        if (_historyTrackSessionId == selected.Segment.SessionId && _historyTrackSource == selected.Segment.Source)
            SetPlaybackPosition(selected.Segment.Start);
    }

    private async void HistorySegmentPlay_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not HistorySegmentItem item) return;
        HistorySegments.SelectedItem = item;
        HistorySegments.ScrollIntoView(item);
        await PlaySelectedSegmentAsync();
        e.Handled = true;
    }

    private void UpdateSelectedSegmentEditor()
    {
        var selected = SelectedHistorySegment();
        var modelRevision = HistoryRevisionSelector.SelectedItem is HistoryRevisionItem;
        CorrectionPanel.IsEnabled = selected is not null && !modelRevision;
        _glossarySuggestions.Clear();
        GlossarySuggestionsPanel.Visibility = Visibility.Collapsed;
        GlossaryNoSuggestionsText.Visibility = Visibility.Collapsed;
        if (selected is null)
        {
            OriginalSegmentText.Clear();
            CorrectedSegmentText.Clear();
            SelectedSegmentAudioText.Text = "Selecciona un fragmento de la transcripción para ubicar su audio exacto.";
            UpdateHistoryControls();
            return;
        }
        OriginalSegmentText.Text = selected.Segment.Text;
        CorrectedSegmentText.Text = selected.Text;
        SelectedSegmentAudioText.Text =
            $"Fragmento seleccionado: {FormatPlaybackTime(selected.Segment.Start)} · {TrackSourceName(selected.Segment.Source)}. Usa “Escuchar fragmento” para reproducir solamente esta parte.";
        if (!modelRevision && selected.Review.LatestRevision is { Action: CorrectionAction.SetText })
        {
            foreach (var candidate in GlossaryCandidateExtractor.Extract(selected.Segment.Text, selected.Text))
                _glossarySuggestions.Add(new(candidate.MistakenForm, candidate.PreferredTerm));

            GlossarySuggestionsPanel.Visibility = _glossarySuggestions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            GlossaryNoSuggestionsText.Visibility = _glossarySuggestions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        UpdateHistoryControls();
    }

    private async Task PlaySelectedSegmentAsync()
    {
        var session = SelectedHistorySession();
        var selected = SelectedHistorySegment();
        if (session is null || selected is null || _store is null || _audioArchive is null || selected.Segment.SessionId != session.Id)
        {
            StatusText.Text = "Selecciona primero un fragmento de la transcripción.";
            return;
        }

        var operation = BeginPlaybackOperation();
        try
        {
            var source = selected.Segment.Source;
            var chunks = await _store.GetArchivedAudioAsync(session.Id, source, _lifetime.Token);
            if (!_playbackOperations.IsCurrent(operation) || SelectedHistorySession()?.Id != session.Id || SelectedHistorySegment()?.Segment.Id != selected.Segment.Id) return;
            if (chunks.Count == 0)
            {
                StatusText.Text = $"No hay audio conservado de {TrackSourceName(source).ToLowerInvariant()} para este fragmento.";
                return;
            }

            var waveform = await AudioWaveformBuilder.BuildAsync(_audioArchive, session.StartedAt, chunks, cancellationToken: _lifetime.Token);
            if (!_playbackOperations.IsCurrent(operation) || SelectedHistorySession()?.Id != session.Id || SelectedHistorySegment()?.Segment.Id != selected.Segment.Id) return;

            SelectHistorySource(source);
            ApplyHistoryTrack(session, source, chunks, waveform);
            ApplyHistoryState(HistoryPresenter.Create(
                true,
                session.State,
                _historyRows.Select(item => item.Segment).ToArray(),
                _historyAudio,
                source,
                FormatTranscript));

            var duration = TimeSpan.FromSeconds(Math.Max(1, (selected.Segment.End - selected.Segment.Start).TotalSeconds));
            await PlayFromPositionAsync(
                session,
                selected.Segment.Start,
                duration,
                $"Fragmento · {TrackSourceName(source)}",
                operation);
        }
        catch (OperationCanceledException) when (!_playbackOperations.IsCurrent(operation)) { }
        catch (OperationCanceledException) { if (_playbackOperations.IsCurrent(operation)) StatusText.Text = "Reproducción detenida"; }
        catch (Exception ex) { if (_playbackOperations.IsCurrent(operation)) ShowError("No se pudo reproducir el audio", ex.Message); }
    }
private async void SaveCorrection_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedHistorySegment();
        var session = SelectedHistorySession();
        if (selected is null || session is null || _store is null || selected.Segment.SessionId != session.Id) return;
        HistoryLoadTicket ticket;
        try { ticket = _historyLoads.Capture(session.Id); }
        catch (OperationCanceledException) { return; }
        try
        {
            await _store.SaveCorrectionAsync(selected.Segment.Id, CorrectedSegmentText.Text, _settings.LocalDisplayName, ticket.CancellationToken);
            if (!_historyLoads.IsCurrent(ticket, SelectedHistorySession()?.Id)) return;
            await LoadHistoryReviewAsync(session, ticket, selected.Segment.Id);
            if (_historyLoads.IsCurrent(ticket, SelectedHistorySession()?.Id))
                StatusText.Text = "Corrección guardada; se conservó el texto original del modelo";
        }
        catch (OperationCanceledException) when (!_historyLoads.IsCurrent(ticket, SelectedHistorySession()?.Id)) { }
        catch (Exception ex) { if (_historyLoads.IsCurrent(ticket, SelectedHistorySession()?.Id)) ShowError("No se pudo guardar la corrección", ex.Message); }
    }

    private async void UndoCorrection_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedHistorySegment();
        var session = SelectedHistorySession();
        if (selected is null || session is null || _store is null || selected.Segment.SessionId != session.Id) return;
        HistoryLoadTicket ticket;
        try { ticket = _historyLoads.Capture(session.Id); }
        catch (OperationCanceledException) { return; }
        try
        {
            await _store.UndoCorrectionAsync(selected.Segment.Id, _settings.LocalDisplayName, ticket.CancellationToken);
            if (!_historyLoads.IsCurrent(ticket, SelectedHistorySession()?.Id)) return;
            await LoadHistoryReviewAsync(session, ticket, selected.Segment.Id);
            if (_historyLoads.IsCurrent(ticket, SelectedHistorySession()?.Id))
                StatusText.Text = "Corrección deshecha; se restauró el texto original del modelo";
        }
        catch (OperationCanceledException) when (!_historyLoads.IsCurrent(ticket, SelectedHistorySession()?.Id)) { }
        catch (Exception ex) { if (_historyLoads.IsCurrent(ticket, SelectedHistorySession()?.Id)) ShowError("No se pudo deshacer la corrección", ex.Message); }
    }

    private async void AddGlossary_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedHistorySegment();
        var session = SelectedHistorySession();
        if (selected?.Review.LatestRevision is not { Action: CorrectionAction.SetText } correction ||
            session is null ||
            _store is null ||
            selected.Segment.SessionId != session.Id)
        {
            StatusText.Text = "Guarda una corrección antes de agregar sus términos al diccionario.";
            return;
        }
        HistoryLoadTicket ticket;
        try { ticket = _historyLoads.Capture(session.Id); }
        catch (OperationCanceledException) { return; }
        try
        {
            var requested = _glossarySuggestions
                .Where(item => item.IsSelected && !string.IsNullOrWhiteSpace(item.MistakenForm) && !string.IsNullOrWhiteSpace(item.PreferredTerm))
                .Where(item => !string.Equals(item.MistakenForm.Trim(), item.PreferredTerm.Trim(), StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (requested.Length == 0)
            {
                StatusText.Text = "Selecciona al menos una palabra modificada antes de agregarla.";
                return;
            }

            var category = (GlossaryCategory.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Término técnico";
            var existing = await _store.ListGlossaryAsync(ticket.CancellationToken);
            var existingPairs = existing
                .Select(item => $"{item.MistakenForm.Trim()}\0{item.PreferredTerm.Trim()}")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var saved = 0;
            var skipped = 0;
            foreach (var item in requested)
            {
                var pair = $"{item.MistakenForm.Trim()}\0{item.PreferredTerm.Trim()}";
                if (!existingPairs.Add(pair))
                {
                    skipped++;
                    continue;
                }

                await _store.AddGlossaryEntryAsync(item.PreferredTerm, item.MistakenForm, category,
                    GlossaryActiveCheck.IsChecked == true, correction.Id, ticket.CancellationToken);
                saved++;
            }

            if (_historyLoads.IsCurrent(ticket, SelectedHistorySession()?.Id))
            {
                foreach (var item in requested) _glossarySuggestions.Remove(item);
                var hasRemaining = _glossarySuggestions.Count > 0;
                GlossarySuggestionsPanel.Visibility = hasRemaining ? Visibility.Visible : Visibility.Collapsed;
                GlossaryNoSuggestionsText.Text = "Todos los reemplazos detectados ya están guardados en el diccionario.";
                GlossaryNoSuggestionsText.Visibility = hasRemaining ? Visibility.Collapsed : Visibility.Visible;
                AddGlossaryButton.IsEnabled = hasRemaining;
                StatusText.Text = saved > 0
                    ? $"{saved} {(saved == 1 ? "término guardado" : "términos guardados")}" + (skipped > 0 ? $"; {skipped} ya existían" : string.Empty)
                    : "Esos reemplazos ya existen en el diccionario.";
            }
        }
        catch (OperationCanceledException) when (!_historyLoads.IsCurrent(ticket, SelectedHistorySession()?.Id)) { }
        catch (Exception ex) { if (_historyLoads.IsCurrent(ticket, SelectedHistorySession()?.Id)) ShowError("No se pudieron agregar los términos al diccionario", ex.Message); }
    }

    private void PopulateHistoryRevisionSelector(IReadOnlyList<TranscriptModelRevision> revisions)
    {
        _historyRevisionLoads.Invalidate();
        _settingHistoryRevision = true;
        try
        {
            HistoryRevisionSelector.Items.Clear();
            HistoryRevisionSelector.Items.Add(new ComboBoxItem { Content = "Original · revisión humana", Tag = "Original" });
            foreach (var revision in revisions) HistoryRevisionSelector.Items.Add(new HistoryRevisionItem(revision));
            HistoryRevisionSelector.SelectedIndex = 0;
        }
        finally { _settingHistoryRevision = false; }
        PopulateComparisonSelectors(revisions);
    }


    private void PopulateComparisonSelectors(IReadOnlyList<TranscriptModelRevision> revisions)
    {
        _settingComparisonSelectors = true;
        try
        {
            ComparisonLeftSelector.Items.Clear();
            ComparisonRightSelector.Items.Clear();
            ComparisonLeftSelector.Items.Add(new ComboBoxItem { Content = "Original · revisión humana" });
            ComparisonRightSelector.Items.Add(new ComboBoxItem { Content = "Original · revisión humana" });
            foreach (var revision in revisions)
            {
                ComparisonLeftSelector.Items.Add(new HistoryRevisionItem(revision));
                ComparisonRightSelector.Items.Add(new HistoryRevisionItem(revision));
            }
            ComparisonLeftSelector.SelectedIndex = 0;
            ComparisonRightSelector.SelectedIndex = revisions.Count > 0 ? revisions.Count : 0;
            _comparisonRows.Clear();
            ComparisonStatusText.Text = revisions.Count == 0
                ? "Retranscribe esta pista para disponer de otra versión."
                : "Elige dos versiones y pulsa Comparar. No se modificará ninguna transcripción.";
        }
        finally { _settingComparisonSelectors = false; }
        UpdateComparisonControls();
    }

    private void ComparisonSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_settingComparisonSelectors || !_initialized) return;
        _comparisonRows.Clear();
        ComparisonStatusText.Text = "Pulsa Comparar para revisar estas dos versiones sin modificarlas.";
        UpdateComparisonControls();
    }

    private void UpdateComparisonControls()
    {
        if (CompareVersionsButton is null) return;
        ComparisonLeftSelector.IsEnabled = _historyState.HasSelection && !_comparisonLoading;
        ComparisonRightSelector.IsEnabled = _historyState.HasSelection && !_comparisonLoading;
        CompareVersionsButton.IsEnabled =
            _historyState.HasSelection &&
            !_comparisonLoading &&
            ComparisonLeftSelector.SelectedItem is not null &&
            ComparisonRightSelector.SelectedItem is not null &&
            ComparisonVersionId(ComparisonLeftSelector) != ComparisonVersionId(ComparisonRightSelector);
    }

    private static string? ComparisonVersionId(ComboBox selector) =>
        (selector.SelectedItem as HistoryRevisionItem)?.Revision.Id;

    private async void CompareVersions_Click(object sender, RoutedEventArgs e)
    {
        var session = SelectedHistorySession();
        if (session is null || _store is null || _comparisonLoading) return;
        var source = SelectedHistorySource();
        var left = ComparisonLeftSelector.SelectedItem as HistoryRevisionItem;
        var right = ComparisonRightSelector.SelectedItem as HistoryRevisionItem;
        if (left?.Revision.Id == right?.Revision.Id)
        {
            ComparisonStatusText.Text = "Elige dos versiones diferentes.";
            return;
        }

        HistoryLoadTicket ticket;
        try { ticket = _historyLoads.Capture(session.Id); }
        catch (OperationCanceledException) { return; }

        _comparisonLoading = true;
        UpdateComparisonControls();
        _comparisonRows.Clear();
        ComparisonStatusText.Text = "Comparando los textos guardados…";
        try
        {
            var first = await ReadComparisonVersionAsync(session, source, left, ticket.CancellationToken);
            var second = await ReadComparisonVersionAsync(session, source, right, ticket.CancellationToken);
            if (!_historyLoads.IsCurrent(ticket, SelectedHistorySession()?.Id) ||
                SelectedHistorySource() != source ||
                ComparisonVersionId(ComparisonLeftSelector) != left?.Revision.Id ||
                ComparisonVersionId(ComparisonRightSelector) != right?.Revision.Id) return;

            var rows = TranscriptComparison.Align(first, second, TimeSpan.FromSeconds(15));
            foreach (var row in rows)
            {
                var hasAudio = _historyTrackSessionId == session.Id &&
                    _historyTrackSource == source &&
                    _historyTrackChunks.Any(chunk =>
                        chunk.StartedAt - session.StartedAt < row.End &&
                        chunk.StartedAt - session.StartedAt + chunk.Duration > row.Start);
                _comparisonRows.Add(row with { HasAudio = hasAudio });
            }
            var firstName = left?.Revision.ModelIdentity ?? "Original revisada";
            var secondName = right?.Revision.ModelIdentity ?? "Original revisada";
            ComparisonStatusText.Text = rows.Count == 0
                ? "Estas versiones no contienen texto para la pista seleccionada."
                : $"{firstName} frente a {secondName} · {rows.Count} intervalos · {TrackSourceName(source)}. Escuchar solo está disponible donde se conservó audio.";
        }
        catch (OperationCanceledException) when (!_historyLoads.IsCurrent(ticket, SelectedHistorySession()?.Id)) { }
        catch (Exception ex)
        {
            if (_historyLoads.IsCurrent(ticket, SelectedHistorySession()?.Id))
                ShowError("No se pudieron comparar las versiones", ex.Message);
        }
        finally
        {
            _comparisonLoading = false;
            UpdateComparisonControls();
        }
    }

    private async Task<IReadOnlyList<ComparisonTextSegment>> ReadComparisonVersionAsync(
        SessionSummary session,
        AudioSourceKind source,
        HistoryRevisionItem? selected,
        CancellationToken cancellationToken)
    {
        if (_store is null) return [];
        if (selected is null)
        {
            var reviewed = await _store.GetReviewedSegmentsAsync(session.Id, cancellationToken);
            return reviewed
                .Where(item => item.Segment.Source == source)
                .Select(item => new ComparisonTextSegment(item.Segment.Start, item.Segment.End, item.EffectiveText))
                .ToArray();
        }
        if (selected.Revision.SessionId != session.Id ||
            selected.Revision.Source != source ||
            selected.Revision.Status != ModelRevisionStatus.Succeeded)
            throw new InvalidOperationException("La versión seleccionada no pertenece a esta pista o sesión.");
        var segments = await _store.GetModelRevisionSegmentsAsync(selected.Revision.Id, cancellationToken);
        return segments
            .Select(item => new ComparisonTextSegment(item.Start, item.End, item.Text))
            .ToArray();
    }

    private async void ComparisonListen_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TranscriptComparisonRow row) return;
        var session = SelectedHistorySession();
        if (session is null || _historyTrackSessionId != session.Id || _historyTrackSource != SelectedHistorySource())
        {
            StatusText.Text = "Selecciona primero la pista de audio correspondiente.";
            return;
        }
        var playablePosition = ResolvePlayablePosition(session, row.Start);
        if (playablePosition is null || playablePosition >= row.End)
        {
            StatusText.Text = "No hay audio conservado para este intervalo.";
            return;
        }
        var operation = BeginPlaybackOperation();
        await PlayFromPositionAsync(
            session,
            playablePosition.Value,
            row.End - playablePosition.Value,
            $"Intervalo comparado · {TrackSourceName(_historyTrackSource)}",
            operation);
        e.Handled = true;
    }

    private async void HistoryRevisionSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settingHistoryRevision || !_initialized || _store is null) return;
        var session = SelectedHistorySession();
        if (session is null) return;
        var selectedRevision = HistoryRevisionSelector.SelectedItem as HistoryRevisionItem;
        var revisionTicket = _historyRevisionLoads.Begin(session.Id, SelectedHistorySource(), selectedRevision?.Revision.Id);
        HistoryLoadTicket historyTicket;
        try { historyTicket = _historyLoads.Capture(session.Id); }
        catch (OperationCanceledException) { return; }

        if (selectedRevision is null)
        {
            try
            {
                await LoadHistoryReviewAsync(
                    session,
                    historyTicket,
                    revisionTicket: revisionTicket,
                    refreshRevisionSelector: false);
            }
            catch (OperationCanceledException) when (!IsCurrentRevisionSelection(revisionTicket)) { }
            catch (Exception ex)
            {
                if (IsCurrentRevisionSelection(revisionTicket)) ShowError("No se pudo cargar la transcripción original", ex.Message);
            }
            return;
        }

        try
        {
            var segments = await _store.GetModelRevisionSegmentsAsync(
                selectedRevision.Revision.Id,
                revisionTicket.CancellationToken);
            if (!_historyLoads.IsCurrent(historyTicket, SelectedHistorySession()?.Id) ||
                !IsCurrentRevisionSelection(revisionTicket)) return;
            _suppressHistorySegmentPlayback = true;
            try
            {
                _historyRows.Clear();
                foreach (var segment in segments)
                {
                    var transcript = new TranscriptSegment(segment.Id, session.Id, selectedRevision.Revision.Source, segment.Sequence, segment.Start, segment.End, segment.Text, selectedRevision.Revision.StartedAt);
                    _historyRows.Add(new(new ReviewedTranscriptSegment(transcript, null), "Versión generada por el modelo · sin correcciones humanas"));
                }
                HistorySegments.SelectedItem = null;
            }
            finally { _suppressHistorySegmentPlayback = false; }
            HistoryEmptyText.Visibility = segments.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            StatusText.Text = $"Viendo la versión generada por el modelo el {selectedRevision.Revision.StartedAt:yyyy-MM-dd HH:mm}; no se aplican correcciones humanas.";
            UpdateSelectedSegmentEditor();
        }
        catch (OperationCanceledException) when (!IsCurrentRevisionSelection(revisionTicket)) { }
        catch (Exception ex)
        {
            if (IsCurrentRevisionSelection(revisionTicket)) ShowError("No se pudo cargar la versión", ex.Message);
        }
    }
    private async void Retranscribe_Click(object sender, RoutedEventArgs e)
    {
        var session = SelectedHistorySession();
        if (session is null || _retranscription is null || _store is null) return;
        HistoryLoadTicket ticket;
        try { ticket = _historyLoads.Capture(session.Id); } catch (OperationCanceledException) { return; }
        var eligibility = HistoryRetranscriptionPresenter.Create(session.State, _historyState.HasSelectedSourceAudio && _historySelectedSourceAudioComplete, _historyRetranscriptionOperation.IsRunning);
        if (!eligibility.CanStart) { StatusText.Text = eligibility.Guidance; return; }
        var modelPath = ModelPathBox.Text.Trim();
        if (!File.Exists(modelPath)) { ShowError("No se pudo retranscribir", "Selecciona primero un archivo de modelo Whisper GGML existente."); return; }
        if (!_historyRetranscriptionOperation.TryBegin([_lifetime.Token, ticket.CancellationToken], out var operation))
        {
            StatusText.Text = "Ya hay una retranscripción en curso.";
            return;
        }
        UpdateHistoryControls();
        try
        {
            StatusText.Text = "Retranscribiendo el audio cifrado conservado…";
            var revision = await _retranscription.RunAsync(session, SelectedHistorySource(), modelPath, _settings.Language, operation.CancellationToken);
            if (!_historyRetranscriptionOperation.IsCurrent(operation) || !_historyLoads.IsCurrent(ticket, SelectedHistorySession()?.Id)) return;
            await LoadHistoryReviewAsync(session, ticket);
            if (!_historyRetranscriptionOperation.IsCurrent(operation) || !_historyLoads.IsCurrent(ticket, SelectedHistorySession()?.Id)) return;
            HistoryRevisionSelector.SelectedItem = HistoryRevisionSelector.Items.OfType<HistoryRevisionItem>().FirstOrDefault(item => item.Revision.Id == revision.Id);
            StatusText.Text = "La retranscripción se completó como una versión separada del modelo.";
        }
        catch (OperationCanceledException) { if (_historyLoads.IsCurrent(ticket, SelectedHistorySession()?.Id)) StatusText.Text = "Retranscripción cancelada; la transcripción original no fue modificada."; }
        catch (Exception ex) { if (_historyLoads.IsCurrent(ticket, SelectedHistorySession()?.Id)) ShowError("No se pudo retranscribir", ex.Message); }
        finally
        {
            if (_historyRetranscriptionOperation.Complete(operation)) UpdateHistoryControls();
        }
    }

    private void CancelRetranscription_Click(object sender, RoutedEventArgs e) => CancelHistoryRetranscription();

    private void CancelHistoryRetranscription() => _historyRetranscriptionOperation.Cancel();
    private async void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_historyPlaying)
        {
            if (_historyPlaybackPaused)
            {
                _playback.Resume();
                _historyPlaybackPaused = false;
                StatusText.Text = "Reproducción reanudada";
            }
            else
            {
                _playback.Pause();
                _historyPlaybackPaused = true;
                StatusText.Text = "Reproducción pausada";
            }
            UpdateHistoryControls();
            return;
        }

        var session = SelectedHistorySession();
        if (session is null || _historyTrackSessionId != session.Id || _historyTrackChunks.Count == 0 || _audioArchive is null)
        {
            StatusText.Text = _historyState.AudioGuidance;
            return;
        }

        var requested = TimeSpan.FromSeconds(PlaybackTimeline.Value);
        if (requested >= _historyTrackDuration - TimeSpan.FromMilliseconds(100))
            requested = TimeSpan.Zero;
        var operation = BeginPlaybackOperation();
        await PlayFromPositionAsync(session, requested, null, TrackSourceName(_historyTrackSource), operation);
    }

    private async void SeekBack_Click(object sender, RoutedEventArgs e) =>
        await SeekToAsync(TimeSpan.FromSeconds(Math.Max(0, PlaybackTimeline.Value - 10)));

    private async void SeekForward_Click(object sender, RoutedEventArgs e) =>
        await SeekToAsync(TimeSpan.FromSeconds(Math.Min(_historyTrackDuration.TotalSeconds, PlaybackTimeline.Value + 10)));

    private async void PlaybackTimeline_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) =>
        await SeekToAsync(TimeSpan.FromSeconds(PlaybackTimeline.Value));

    private async void PlaybackTimeline_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Left or Key.Right or Key.Home or Key.End or Key.PageDown or Key.PageUp)) return;
        await SeekToAsync(TimeSpan.FromSeconds(PlaybackTimeline.Value));
    }

    private async Task SeekToAsync(TimeSpan requested)
    {
        requested = requested < TimeSpan.Zero ? TimeSpan.Zero : requested > _historyTrackDuration ? _historyTrackDuration : requested;
        if (!_historyPlaying)
        {
            SetPlaybackPosition(requested);
            return;
        }

        var session = SelectedHistorySession();
        if (session is null) return;
        var operation = BeginPlaybackOperation();
        SetPlaybackPosition(requested);
        await PlayFromPositionAsync(session, requested, null, TrackSourceName(_historyTrackSource), operation);
    }

    private async Task PlayFromPositionAsync(
        SessionSummary session,
        TimeSpan requestedPosition,
        TimeSpan? requestedDuration,
        string contextLabel,
        long operation)
    {
        if (_audioArchive is null || _historyTrackSessionId != session.Id || _historyTrackChunks.Count == 0) return;
        try
        {
            var playablePosition = ResolvePlayablePosition(session, requestedPosition);
            if (playablePosition is null)
            {
                StatusText.Text = "No hay audio conservado en esa posición.";
                return;
            }

            var maximumDuration = requestedDuration
                ?? (_historyTrackDuration > playablePosition.Value ? _historyTrackDuration - playablePosition.Value : TimeSpan.Zero);
            if (maximumDuration <= TimeSpan.Zero) return;
            var plan = SegmentAudioNavigator.CreatePlan(
                session.StartedAt,
                playablePosition.Value,
                _historyTrackChunks,
                maximumDuration);
            if (plan is null)
            {
                StatusText.Text = "No hay audio conservado en esa posición.";
                return;
            }

            _playbackBasePosition = playablePosition.Value;
            SetPlaybackPosition(playablePosition.Value);
            _historyPlaying = true;
            _historyPlaybackPaused = false;
            _playbackTimer.Start();
            StatusText.Text = $"Reproduciendo {contextLabel.ToLowerInvariant()} desde {FormatPlaybackTime(playablePosition.Value)}";
            UpdateHistoryControls();

            await _playback.PlayFromAsync(
                _audioArchive,
                plan.Chunks,
                plan.OffsetIntoFirstChunk,
                plan.MaximumDuration,
                _lifetime.Token);
            if (_playbackOperations.IsCurrent(operation))
                StatusText.Text = $"Finalizó la reproducción de {contextLabel.ToLowerInvariant()}";
        }
        catch (OperationCanceledException) when (!_playbackOperations.IsCurrent(operation)) { }
        catch (OperationCanceledException)
        {
            if (_playbackOperations.IsCurrent(operation)) StatusText.Text = "Reproducción detenida";
        }
        catch (Exception ex)
        {
            if (_playbackOperations.IsCurrent(operation)) ShowError("No se pudo reproducir el audio", ex.Message);
        }
        finally
        {
            if (_playbackOperations.IsCurrent(operation))
            {
                SetPlaybackPosition(_playbackBasePosition + _playback.Elapsed);
                _historyPlaying = false;
                _historyPlaybackPaused = false;
                _playbackTimer.Stop();
                UpdateHistoryControls();
            }
        }
    }

    private TimeSpan? ResolvePlayablePosition(SessionSummary session, TimeSpan requested)
    {
        var ordered = _historyTrackChunks.OrderBy(chunk => chunk.StartedAt).ThenBy(chunk => chunk.Sequence).ToArray();
        if (ordered.Length == 0) return null;
        var target = session.StartedAt + requested;
        var containing = ordered.FirstOrDefault(chunk => target >= chunk.StartedAt && target < chunk.StartedAt + chunk.Duration);
        if (containing is not null) return requested;
        var next = ordered.FirstOrDefault(chunk => chunk.StartedAt >= target);
        return next is null ? null : next.StartedAt - session.StartedAt;
    }

    private long BeginPlaybackOperation()
    {
        var operation = _playbackOperations.Begin();
        if (_historyPlaying) SetPlaybackPosition(_playbackBasePosition + _playback.Elapsed);
        _playback.Stop();
        _playbackTimer.Stop();
        _historyPlaying = false;
        _historyPlaybackPaused = false;
        UpdateHistoryControls();
        return operation;
    }

    private void PlaybackTimer_Tick(object? sender, EventArgs e)
    {
        if (!_historyPlaying) return;
        SetPlaybackPosition(_playbackBasePosition + _playback.Elapsed);
    }

    private void StopPlayback_Click(object sender, RoutedEventArgs e)
    {
        if (!_historyPlaying) { StatusText.Text = "No hay audio en reproducción"; return; }
        SupersedePlayback();
        StatusText.Text = "Reproducción detenida";
    }

    private void SupersedePlayback()
    {
        if (_historyPlaying) SetPlaybackPosition(_playbackBasePosition + _playback.Elapsed);
        _playbackOperations.Supersede();
        _playback.Stop();
        _playbackTimer.Stop();
        _historyPlaying = false;
        _historyPlaybackPaused = false;
        UpdateHistoryControls();
    }

    private void ApplyHistoryTrack(
        SessionSummary session,
        AudioSourceKind source,
        IReadOnlyList<ArchivedAudioChunk> chunks,
        IReadOnlyList<double> waveform)
    {
        var trackChanged = _historyTrackSessionId != session.Id || _historyTrackSource != source;
        _historyTrackSessionId = session.Id;
        _historyTrackSource = source;
        _historyTrackChunks = chunks;
        _historyTrackDuration = AudioWaveformBuilder.GetTimelineDuration(session.StartedAt, chunks);
        _waveformBars.Clear();
        foreach (var height in waveform) _waveformBars.Add(height);
        PlaybackTimeline.Maximum = Math.Max(1, _historyTrackDuration.TotalSeconds);
        PlaybackTrackText.Text = chunks.Count == 0
            ? $"{TrackSourceName(source)} · sin audio conservado"
            : $"{TrackSourceName(source)} · {FormatPlaybackTime(_historyTrackDuration)} conservados";
        if (trackChanged) SetPlaybackPosition(TimeSpan.Zero);
        else SetPlaybackPosition(TimeSpan.FromSeconds(Math.Min(PlaybackTimeline.Value, _historyTrackDuration.TotalSeconds)));
    }

    private void SelectHistorySource(AudioSourceKind source)
    {
        _settingHistorySource = true;
        try
        {
            HistoryAudioSource.SelectedItem = HistoryAudioSource.Items.Cast<ComboBoxItem>()
                .First(item => string.Equals(item.Tag?.ToString(), source.ToString(), StringComparison.Ordinal));
        }
        finally { _settingHistorySource = false; }
    }

    private void SetPlaybackPosition(TimeSpan position)
    {
        if (_historyTrackDuration <= TimeSpan.Zero) position = TimeSpan.Zero;
        else if (position < TimeSpan.Zero) position = TimeSpan.Zero;
        else if (position > _historyTrackDuration) position = _historyTrackDuration;
        PlaybackTimeline.Value = position.TotalSeconds;
        PlaybackPositionText.Text = $"{FormatPlaybackTime(position)} / {FormatPlaybackTime(_historyTrackDuration)}";
    }

    private static string FormatPlaybackTime(TimeSpan value) =>
        value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss") : value.ToString(@"mm\:ss");

    private static string TrackSourceName(AudioSourceKind source) =>
        source == AudioSourceKind.Microphone ? "Micrófono · mi voz" : "Audio del equipo · participantes";
    private async void ExportWav_Click(object sender, RoutedEventArgs e)
    {
        var session = SelectedHistorySession();
        if (session is null || _audioArchive is null || !_historyState.CanExportWav) { StatusText.Text = _historyState.AudioGuidance; return; }
        if (MessageBox.Show(this, "El archivo WAV exportado quedará sin cifrar y otras aplicaciones podrán reproducirlo. Si la aplicación se cierra inesperadamente durante la exportación, podría quedar un archivo incompleto. ¿Deseas continuar?", "Exportar audio sin cifrar", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var source = SelectedHistorySource();
        var dialog = new SaveFileDialog { FileName = $"{SafeFileName(session.Title)}-{(source == AudioSourceKind.Microphone ? "microfono" : "audio-equipo")}.wav", Filter = "Audio WAV (*.wav)|*.wav" };
        if (dialog.ShowDialog(this) != true) return;
        try { await _audioArchive.ExportWavAsync(session.Id, source, dialog.FileName, _lifetime.Token); StatusText.Text = "Archivo WAV sin cifrar exportado"; }
        catch (Exception ex) { ShowError("No se pudo exportar el audio", ex.Message); }
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var session = SelectedHistorySession();
        if (session is null || _store is null || !_historyState.CanExportTxt) { StatusText.Text = _historyState.HasSelection ? HistoryPresenter.NoTranscriptMessage : HistoryPresenter.SelectSessionMessage; return; }
        try
        {
            var warning = MessageBox.Show(this, "El archivo TXT exportado no estará cifrado. ¿Deseas continuar?", "Exportación sin cifrar", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (warning != MessageBoxResult.Yes) return;
            var dialog = new SaveFileDialog { FileName = SafeFileName(session.Title) + ".txt", Filter = "Archivo de texto (*.txt)|*.txt" };
            if (dialog.ShowDialog(this) != true) return;
            var segments = await _store.GetReviewedSegmentsAsync(session.Id);
            await File.WriteAllTextAsync(dialog.FileName, TranscriptExport.CreatePlainText(segments), new UTF8Encoding(true));
            StatusText.Text = "Transcripción exportada";
        }
        catch (Exception ex) { ShowError("No se pudo exportar la transcripción", ex.Message); }
    }

    private async void ExportObsidian_Click(object sender, RoutedEventArgs e)
    {
        var session = SelectedHistorySession();
        if (session is null || _store is null || !_historyState.CanExportMarkdown)
        {
            StatusText.Text = _historyState.HasSelection ? HistoryPresenter.NoTranscriptMessage : HistoryPresenter.SelectSessionMessage;
            return;
        }

        try
        {
            var warning = MessageBox.Show(
                this,
                ObsidianMarkdownExport.PlaintextSyncWarning,
                "Exportar nota sin cifrar",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (warning != MessageBoxResult.Yes) return;

            var settings = ObsidianMarkdownExport.DialogSettings(session.Title);
            var dialog = new SaveFileDialog
            {
                FileName = settings.FileName,
                DefaultExt = settings.DefaultExtension,
                Filter = settings.Filter,
                AddExtension = settings.AddExtension,
                OverwritePrompt = settings.OverwritePrompt
            };
            if (dialog.ShowDialog(this) != true) return;

            var segments = await _store.GetReviewedSegmentsAsync(session.Id, _lifetime.Token);
            var version = typeof(MainWindow).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? typeof(MainWindow).Assembly.GetName().Version?.ToString()
                ?? "desconocida";
            var markdown = ObsidianMarkdownExport.Create(session, segments, version);
            await File.WriteAllTextAsync(dialog.FileName, markdown, ObsidianMarkdownExport.Utf8WithoutBom, _lifetime.Token);
            StatusText.Text = "Nota Markdown exportada";
        }
        catch (Exception ex) { ShowError("No se pudo exportar la nota Markdown", ex.Message); }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        var session = SelectedHistorySession();
        if (session is null || _store is null) { StatusText.Text = HistoryPresenter.SelectSessionMessage; return; }
        if (session.State is SessionState.Recording or SessionState.Paused || string.Equals(_coordinator?.ActiveSessionId, session.Id, StringComparison.Ordinal))
        {
            const string message = "Esta sesión sigue activa. Detenla antes de eliminarla.";
            StatusText.Text = message;
            HistoryActionGuidance.Text = message;
            return;
        }
        try
        {
            if (MessageBox.Show(this, $"¿Eliminar definitivamente '{session.Title}', su transcripción y todo el audio conservado? Esta acción no se puede deshacer.", "Eliminar sesión guardada", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            _playback.Stop();
            if (_audioArchive is not null) await _audioArchive.DeleteSessionAsync(session.Id);
            else await _store.DeleteSessionAsync(session.Id);
            ClearHistoryReview();
            HistoryAudioSummary.Text = HistoryPresenter.SelectSessionMessage;
            StatusText.Text = "Sesión eliminada";
            await RefreshHistoryAsync();
        }
        catch (Exception ex) { ShowError("No se pudo eliminar la sesión", ex.Message); }
    }

    private async void ChangeStorageFolder_Click(object sender, RoutedEventArgs e) => await RunExclusiveAsync(async () =>
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Elegir la carpeta principal para los datos de Trazio",
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;
        var target = StorageMigrationService.TargetUnderParent(dialog.FolderName);
        if (MessageBox.Show(this,
            $"Trazio usará esta carpeta después del próximo reinicio:\n\n{target}\n\nSe trasladarán la base de datos cifrada, el audio, el modelo, la configuración y la clave. Los archivos TXT, Markdown y WAV exportados no se trasladarán. ¿Deseas continuar?",
            "Programar traslado de almacenamiento", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await Task.Run(() => _storageMigration.Schedule(ApplicationPaths.DataDirectory, target, AppContext.BaseDirectory));
        UpdateStorageLocationUi();
        StatusText.Text = "Traslado programado. Reinicia Trazio para mover los datos protegidos.";
    });

    private async void RestoreDefaultStorage_Click(object sender, RoutedEventArgs e) => await RunExclusiveAsync(async () =>
    {
        var target = ApplicationPaths.DefaultDataDirectory;
        if (MessageBox.Show(this,
            $"Trazio devolverá sus datos protegidos a la carpeta predeterminada después del próximo reinicio:\n\n{target}\n\nLos archivos TXT, Markdown y WAV exportados no se trasladarán. ¿Deseas continuar?",
            "Restaurar almacenamiento predeterminado", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await Task.Run(() => _storageMigration.Schedule(ApplicationPaths.DataDirectory, target, AppContext.BaseDirectory));
        UpdateStorageLocationUi();
        StatusText.Text = "Regreso a la carpeta predeterminada programado. Reinicia Trazio para continuar.";
    });

    private async void CancelStorageChange_Click(object sender, RoutedEventArgs e) => await RunExclusiveAsync(async () =>
    {
        var pending = _dataRootLocator.Load().Pending;
        if (pending is null) { StatusText.Text = "No hay ningún traslado de almacenamiento pendiente"; UpdateStorageLocationUi(); return; }
        await Task.Run(() => _storageMigration.CancelScheduled(pending));
        UpdateStorageLocationUi();
        StatusText.Text = "Traslado de almacenamiento pendiente cancelado";
    });

    private void UpdateStorageLocationUi()
    {
        StoragePathText.Text = ApplicationPaths.DataDirectory;
        UpdateStorageControls();
    }

    private void UpdateStorageControls()
    {
        if (!IsInitialized) return;
        var view = StorageLocationPresenter.Create(
            ApplicationPaths.DataDirectory,
            ApplicationPaths.DefaultDataDirectory,
            _dataRootLocator.Load(),
            _initialized,
            _recording,
            _historyPlaying,
            _busy,
            _closing);
        StorageLocationStatusText.Text = view.Status;
        ChangeStorageButton.IsEnabled = view.CanChange;
        RestoreDefaultStorageButton.IsEnabled = view.CanRestoreDefault;
        CancelStorageChangeButton.IsEnabled = view.CanCancelPending;
        OpenStorageButton.IsEnabled = !_busy && !_closing;
        CopyStoragePathButton.IsEnabled = !_busy && !_closing;
    }

    private void OpenStorageFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(ApplicationPaths.DataDirectory);
            Process.Start(new ProcessStartInfo { FileName = ApplicationPaths.DataDirectory, UseShellExecute = true });
            StatusText.Text = "Carpeta de almacenamiento local abierta";
        }
        catch (Exception ex) { ShowError("No se pudo abrir la carpeta de almacenamiento", ex.Message); }
    }

    private void CopyStoragePath_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(ApplicationPaths.DataDirectory); StatusText.Text = "Ruta de almacenamiento copiada"; }
        catch (Exception ex) { ShowError("No se pudo copiar la ruta de almacenamiento", ex.Message); }
    }

    private void ApplyHistoryState(HistoryViewState state)
    {
        _historyState = state;
        if (!state.HasSelection) HistoryAudioSummary.Text = HistoryPresenter.SelectSessionMessage;
        if (SelectedHistorySource() != state.SelectedSource)
        {
            _settingHistorySource = true;
            try
            {
                HistoryAudioSource.SelectedItem = HistoryAudioSource.Items.Cast<ComboBoxItem>()
                    .First(item => string.Equals(item.Tag?.ToString(), state.SelectedSource.ToString(), StringComparison.Ordinal));
            }
            finally { _settingHistorySource = false; }
        }
        HistoryAudioGuidance.Text = state.AudioGuidance;
        HistoryActionGuidance.Text = state.ActionGuidance;
        UpdateHistoryControls();
    }

    private void UpdateHistoryControls()
    {
        var selected = SelectedHistorySegment();
        var hasTrack = _historyTrackChunks.Count > 0 && _historyTrackDuration > TimeSpan.Zero;
        HistoryAudioSource.IsEnabled = _historyState.CanChooseSource && !_historyPlaying && !_historyRetranscriptionOperation.IsRunning;
        HistoryRevisionSelector.IsEnabled = _historyState.HasSelection && !_historyPlaying && !_historyRetranscriptionOperation.IsRunning;
        PlayPauseButton.IsEnabled = _historyState.CanPlayAudio && hasTrack;
        PlayPauseButton.Content = _historyPlaying
            ? (_historyPlaybackPaused ? "Continuar" : "Pausar")
            : "Reproducir";
        SeekBackButton.IsEnabled = _historyState.CanPlayAudio && hasTrack;
        SeekForwardButton.IsEnabled = _historyState.CanPlayAudio && hasTrack;
        StopPlaybackButton.IsEnabled = _historyPlaying;
        PlaybackTimeline.IsEnabled = _historyState.CanPlayAudio && hasTrack;
        ExportWavButton.IsEnabled = _historyState.CanExportWav && !_historyPlaying;
        ExportTxtButton.IsEnabled = _historyState.CanExportTxt;
        ExportObsidianButton.IsEnabled = _historyState.CanExportMarkdown;
        DeleteSessionButton.IsEnabled = _historyState.CanDelete && !_historyPlaying && !_historyRetranscriptionOperation.IsRunning;
        var viewingModelRevision = HistoryRevisionSelector.SelectedItem is HistoryRevisionItem;
        SaveCorrectionButton.IsEnabled = selected is not null && !viewingModelRevision;
        UndoCorrectionButton.IsEnabled = selected?.Review.IsCorrected == true && !viewingModelRevision;
        AddGlossaryButton.IsEnabled = _glossarySuggestions.Count > 0 && !viewingModelRevision;
        var retranscription = HistoryRetranscriptionPresenter.Create(
            SelectedHistorySession()?.State,
            _historyState.HasSelectedSourceAudio && _historySelectedSourceAudioComplete,
            _historyRetranscriptionOperation.IsRunning);
        RetranscribeButton.IsEnabled = retranscription.CanStart && !_historyPlaying;
        CancelRetranscriptionButton.IsEnabled = retranscription.CanCancel;
        RetranscribeButton.ToolTip = retranscription.Guidance;
        CancelRetranscriptionButton.ToolTip = retranscription.Guidance;

        PlayPauseButton.ToolTip = _historyState.CanPlayAudio
            ? $"Reproduce o pausa la pista {TrackSourceName(_historyTrackSource).ToLowerInvariant()}."
            : _historyState.AudioGuidance;
        SeekBackButton.ToolTip = "Retrocede 10 segundos en la pista seleccionada.";
        SeekForwardButton.ToolTip = "Avanza 10 segundos en la pista seleccionada.";
        ExportWavButton.ToolTip = _historyState.CanExportWav
            ? $"Crea un archivo WAV sin cifrar de {TrackSourceName(_historyTrackSource).ToLowerInvariant()} en la ubicación que elijas."
            : _historyState.AudioGuidance;
        ExportTxtButton.ToolTip = _historyState.CanExportTxt
            ? "Crea una transcripción TXT sin cifrar en la ubicación que elijas."
            : (_historyState.HasSelection ? HistoryPresenter.NoTranscriptMessage : HistoryPresenter.SelectSessionMessage);
        ExportObsidianButton.ToolTip = _historyState.CanExportMarkdown
            ? "Crea una nota Markdown sin cifrar compatible con Obsidian en la ubicación que elijas."
            : (_historyState.HasSelection ? HistoryPresenter.NoTranscriptMessage : HistoryPresenter.SelectSessionMessage);
        DeleteSessionButton.ToolTip = _historyState.DeleteReason;
        UpdateComparisonControls();
        UpdateStorageControls();
    }
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closingConfirmed) return;
        e.Cancel = true;
        if (_closing) return;
        if (_recording && MessageBox.Show(this, "Hay una transcripción activa. ¿Deseas detenerla y salir?", "Sesión activa", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _closing = true;
        SetRecordingButtons(_recording, _recordingPaused);
        _downloadCancellation?.Cancel();
        try
        {
            await VisualCaptureShutdown.RunVisualFirstAsync(
                () => RunVisualCaptureActionAsync(() => StopAndDisposeVisualCaptureAsync(VisualCaptureState.Stopped)),
                async () =>
                {
                    if (!_recording) _lifetime.Cancel();
                    await _operation;
                    await _historyRetranscriptionOperation.CancelAndWaitAsync();
                    if (_recording) await (_coordinator?.StopAsync() ?? Task.CompletedTask);
                    if (_coordinator is not null) await _coordinator.DisposeAsync();
                    await _capture.DisposeAsync();
                    await _playback.DisposeAsync();
                    _historyRetranscriptionOperation.Dispose();
                    _protector?.Dispose();
                });
        }
        catch (Exception ex) { StatusText.Text = "Cierre interrumpido: " + ex.Message; }
        finally
        {
            _meetingWindowMonitorTimer.Stop();
            _meetingWindowSelection.FinishSession();
            InvalidateVisualAuthorization();
            _closingConfirmed = true;
            Close();
        }
    }

    private AppSettings ReadSettings() => new(
        MicrophoneBox.SelectedValue as string,
        OutputBox.SelectedValue as string,
        ModelPathBox.Text.Trim(),
        (LanguageBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "es",
        MicrophoneCheck.IsChecked == true,
        OutputCheck.IsChecked == true,
        AudioRetentionPolicy.Required,
        SelectedAudioBudget(),
        LocalDisplayNameBox.Text.Trim(),
        string.IsNullOrWhiteSpace(LocalOrganizationBox.Text) ? null : LocalOrganizationBox.Text.Trim(),
        ConfirmLocalProfileCheck.IsChecked == true);

    private void SelectLanguage(string language)
    {
        LanguageBox.SelectedItem = LanguageBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, language)) ?? LanguageBox.Items[0];
    }

    private static string DefaultSessionTitle()
    {
        var localNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.Local);
        return $"Reunión {localNow:yyyy-MM-dd HH:mm}";
    }

    private int SelectedAudioBudget() => int.TryParse((AudioBudgetBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var value) && value is 1 or 2 or 5 ? value : 1;
    private void SelectAudioBudget(int budget) => AudioBudgetBox.SelectedItem = AudioBudgetBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag?.ToString(), budget.ToString())) ?? AudioBudgetBox.Items[0];
    private AudioSourceKind SelectedHistorySource() => (HistoryAudioSource.SelectedItem as ComboBoxItem)?.Tag?.ToString() == nameof(AudioSourceKind.SystemOutput) ? AudioSourceKind.SystemOutput : AudioSourceKind.Microphone;
    private SessionSummary? SelectedHistorySession() => (HistoryList.SelectedItem as HistorySessionItem)?.Session;
    private HistorySegmentItem? SelectedHistorySegment() => HistorySegments.SelectedItem as HistorySegmentItem;
    private string? SelectedHistoryRevisionId() => (HistoryRevisionSelector.SelectedItem as HistoryRevisionItem)?.Revision.Id;
    private bool IsCurrentRevisionSelection(RevisionSelectionTicket ticket) =>
        _historyRevisionLoads.IsCurrent(ticket, SelectedHistorySession()?.Id, SelectedHistorySource(), SelectedHistoryRevisionId());

    private void ClearHistoryReview()
    {
        _historyRows.Clear();
        _comparisonRows.Clear();
        _settingComparisonSelectors = true;
        try
        {
            ComparisonLeftSelector.Items.Clear();
            ComparisonRightSelector.Items.Clear();
        }
        finally { _settingComparisonSelectors = false; }
        ComparisonStatusText.Text = "Selecciona una sesión y retranscribe una pista para comparar versiones.";
        _historyAudio = [];
        _historyTrackChunks = [];
        _historyTrackDuration = TimeSpan.Zero;
        _historyTrackSessionId = null;
        _waveformBars.Clear();
        _historySelectedSourceAudioComplete = false;
        PlaybackTrackText.Text = "Selecciona una sesión y una pista de audio.";
        SetPlaybackPosition(TimeSpan.Zero);
        HistoryEmptyText.Visibility = Visibility.Visible;
        UpdateSelectedSegmentEditor();
    }

    private static bool IsCompleteRetainedSource(IReadOnlyList<ArchivedAudioChunk> chunks, DateTimeOffset sessionStartedAt)
    {
        if (chunks.Count == 0) return false;
        try { RetainedAudioContinuity.Validate(chunks, sessionStartedAt); return true; }
        catch (InvalidOperationException) { return false; }
    }
    private static string SourceName(AudioSourceKind source) => source == AudioSourceKind.Microphone ? "Micrófono" : "Audio del equipo";
    private static string FormatTranscript(IEnumerable<TranscriptSegment> segments) => TranscriptPresentation.Format(segments);
    private static string SafeFileName(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
    private void ShowError(string title, string message) { StatusText.Text = message; MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Error); }

    private sealed record TranscriptRow(string Header, string Text);
}













