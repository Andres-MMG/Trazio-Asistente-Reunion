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
    private readonly ObservableCollection<HistorySearchResultItem> _historySearchRows = [];
    private readonly ObservableCollection<PendingReviewItem> _pendingReviewRows = [];
    private readonly ObservableCollection<TranscriptComparisonRow> _comparisonRows = [];
    private readonly ObservableCollection<GlossaryWorkspaceItem> _glossaryWorkspaceRows = [];
    private IReadOnlyList<GlossaryEntry> _glossaryWorkspaceEntries = [];
    private bool _settingGlossaryFilters;
    private bool _glossaryWorkspaceLoadFailed;
    private bool _settingComparisonSelectors;
    private bool _comparisonLoading;
    private readonly ObservableCollection<GlossarySuggestionItem> _glossarySuggestions = [];
    private readonly ObservableCollection<double> _waveformBars = [];
    private IReadOnlyList<AudioArchiveSummary> _historyAudio = [];
    private IReadOnlyList<ArchivedAudioChunk> _historyTrackChunks = [];
    private TimeSpan _historyTrackDuration;
    private PlaybackTimelineMap? _historyTrackTimeline;
    private TimeSpan _playbackBasePosition;
    private PlaybackTimelineMap? _activePlaybackTimeline;
    private AudioSourceKind? _activePlaybackSource;
    private string? _activePlaybackSessionId;
    private PlaybackOperationRun? _activePlaybackOperation;
    private PlaybackRequestContext? _activePlaybackRequest;
    private PlaybackSpeed _playbackSpeed = PlaybackSpeed.Normal;
    private HistorySegmentItem? _highlightedHistoryRow;
    private AudioSourceKind _historyTrackSource = AudioSourceKind.Microphone;
    private string? _historyTrackSessionId;
    private readonly DispatcherTimer _playbackTimer;
    private readonly IMeetingWindowCatalog _meetingWindowCatalog;
    private readonly MeetingWindowSelectionController _meetingWindowSelection;
    private readonly DispatcherTimer _meetingWindowMonitorTimer;
    private readonly VisualCaptureStateTracker _visualCaptureStateTracker = new();
    private readonly SemaphoreSlim _visualCaptureActions = new(1, 1);
    private VisualCaptureAuthorization? _visualCaptureAuthorization;
    private AnonymousVisualAnalysisAuthorization? _anonymousVisualAnalysisAuthorization;
    private VisualCaptureSessionController? _visualCaptureController;
    private IAnonymousVisualAnalysisSession? _anonymousVisualAnalysisSession;
    private ISessionTimelineContext? _anonymousVisualAnalysisContext;
    private VisualProbeProfileValidationState? _anonymousVisualAnalysisProfileValidationState;
    private VisualCaptureState _visualCaptureState = VisualCaptureState.Off;
    private AnonymousVisualAnalysisStatus _anonymousVisualAnalysisStatus = AnonymousVisualAnalysisStatus.Off;
    private bool _visualCaptureActionInProgress;
    private bool _visualPausedByRecording;
    private int _anonymousVisualAnalysisFailureReported;
    private bool _historySelectedSourceAudioComplete;
    private bool _suppressHistorySegmentPlayback;
    private bool _historyPlaybackPaused;
    private bool _activePlaybackAllowsSeeking;
    private bool _historyDeleteInProgress;
    private readonly HistorySelectionCoordinator _historyLoads = new();
    private readonly HistorySearchCoordinator _historySearch = new();
    private readonly HistorySearchNavigationCoordinator _historySearchNavigation = new();
    private readonly HistorySearchActivityGate _historySearchActivity = new();
    private readonly RevisionSelectionCoordinator _historyRevisionLoads = new();
    private readonly PlaybackOperationCoordinator _playbackOperations = new();
    private readonly PlaybackTransitionGate _playbackTransitions = new();
    private readonly PlaybackTransitionEpoch _playbackTransitionEpoch = new();
    private readonly PlaybackSpeedChangeCoordinator _playbackSpeedChanges = new();
    private AesContentProtector? _protector;
    private SqliteSessionStore? _store;
    private AnonymousVisualEvidenceProjector? _anonymousVisualEvidenceProjector;
    private int _activeVisualEvidenceRefreshScheduled;
    private int _historyVisualEvidenceRefreshScheduled;
    private string? _pendingActiveVisualEvidenceSessionId;
    private string? _pendingHistoryVisualEvidenceSessionId;
    private AudioArchiveStore? _audioArchive;
    private HistoryRetranscriptionService? _retranscription;
    private readonly OwnedCancellationOperationCoordinator _historyRetranscriptionOperation = new();
    private readonly OwnedCancellationOperationCoordinator _glossaryWorkspaceOperation = new();
    private readonly OwnedCancellationOperationCoordinator _pendingReviewOperation = new();
    private readonly OwnedCancellationOperationCoordinator _historyNavigationOperation = new();
    private readonly PendingReviewCoordinator _pendingReviewLoads = new();
    private readonly CorrectionDraftNavigationGuard _correctionDraftNavigation = new();
    private readonly SemaphoreSlim _pendingReviewTransition = new(1, 1);
    private readonly SemaphoreSlim _historyNavigationTransition = new(1, 1);
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
    private bool _suppressHistoryListSelectionChanged;
    private bool _suppressPendingReviewSelectionChanged;
    private bool _suppressHistorySegmentSelectionChanged;
    private bool _suppressHistorySearchResultSelectionChanged;
    private bool _settingCorrectionEditor;
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
        HistorySearchResults.ItemsSource = _historySearchRows;
        PendingReviewList.ItemsSource = _pendingReviewRows;
        ComparisonRows.ItemsSource = _comparisonRows;
        GlossarySuggestionsList.ItemsSource = _glossarySuggestions;
        GlossaryWorkspaceList.ItemsSource = _glossaryWorkspaceRows;
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
        _anonymousVisualEvidenceProjector = new(_store.GetAnonymousVisualEvidenceAsync);
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
        UpdateHistorySearchControls();
        UpdatePendingReviewControls();
        UpdateGlossaryWorkspaceControls();
        UpdateStorageControls();
        UpdateVisualCaptureUi();
        if (ReferenceEquals(MainTabs.SelectedItem, GlossaryTabItem)) await LoadGlossaryWorkspaceAsync();
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
            StatusText.Text = "Reanuda la reunión antes de autorizar la captura visual.";
            UpdateVisualCaptureUi();
            return;
        }
        var selection = _meetingWindowSelection.Selection;
        if (!IsCurrentVisualSelectionAvailable(selection))
        {
            InvalidateVisualAuthorization();
            UpdateVisualCaptureUi();
            StatusText.Text = "Selecciona una ventana disponible antes de autorizar la captura visual.";
            return;
        }

        var dialog = new VisualCaptureConsentWindow { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            AuthorizeVisualCaptureButton.Focus();
            StatusText.Text = "No se activó la captura visual. La transcripción continuará sin cambios.";
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

    private void AuthorizeAnonymousVisualAnalysis_Click(object sender, RoutedEventArgs e)
    {
        if (_visualCaptureActionInProgress || _busy || _closing) return;
        var selection = _meetingWindowSelection.Selection;
        if (!TryResolveAnonymousVisualAnalysisContext(
                selection,
                out var sessionId,
                out var timelineContext))
        {
            InvalidateAnonymousVisualAnalysisAuthorization();
            UpdateVisualCaptureUi();
            StatusText.Text = "El análisis anónimo no puede autorizarse en el estado actual. El audio y la transcripción continúan.";
            return;
        }

        var dialog = new AnonymousVisualAnalysisConsentWindow { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            AuthorizeAnonymousVisualAnalysisButton.Focus();
            StatusText.Text = "No se autorizó el análisis visual anónimo. El audio y la transcripción continúan sin cambios.";
            return;
        }

        if (!TryResolveAnonymousVisualAnalysisContext(
                selection,
                out var currentSessionId,
                out var currentTimelineContext) ||
            !string.Equals(currentSessionId, sessionId, StringComparison.Ordinal) ||
            !ReferenceEquals(currentTimelineContext, timelineContext))
        {
            InvalidateAnonymousVisualAnalysisAuthorization();
            UpdateVisualCaptureUi();
            StatusText.Text = "La sesión o la ventana cambió. Autoriza nuevamente el análisis anónimo.";
            return;
        }

        _anonymousVisualAnalysisAuthorization =
            AnonymousVisualAnalysisAuthorization.GrantForSession(
                selection!,
                sessionId,
                AnonymousVisualAnalysisScope.ActivityAndAvailabilityIntervals);
        _anonymousVisualAnalysisStatus = AnonymousVisualAnalysisStatus.Authorized;
        UpdateVisualCaptureUi();
        AnonymousVisualAnalysisStatusText.Focus();
    }

    private async void Start_Click(object sender, RoutedEventArgs e) => await RunExclusiveAsync(StartSessionAsync);

    private async Task StartSessionAsync()
    {
        RecordingCoordinator? candidate = null;
        MeetingWindowSelection? visualSelection = null;
        var audioStarted = false;
        try
        {
            InvalidateAnonymousVisualAnalysisAuthorization();
            _anonymousVisualAnalysisStatus = AnonymousVisualAnalysisStatus.Off;
            _anonymousVisualAnalysisProfileValidationState = null;
            Interlocked.Exchange(ref _anonymousVisualAnalysisFailureReported, 0);
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
        InvalidateVisualAuthorization();
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
                StatusText.Text = "La captura o la evidencia visual no pudo continuar. El audio y la transcripción siguen activos.";
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
        var captureAuthorization = _visualCaptureAuthorization;
        if (captureAuthorization is null) return;

        var sessionIdText = _coordinator?.ActiveSessionId;
        if (!Guid.TryParseExact(sessionIdText, "N", out var sessionId) ||
            _meetingWindowSelection.Selection != selection ||
            !IsCurrentVisualSelectionAvailable(selection) ||
            !captureAuthorization.TryConsume(selection, sessionId, out var consent) ||
            consent is null)
        {
            InvalidateVisualAuthorization();
            SetVisualCaptureState(VisualCaptureState.Off);
            StatusText.Text = "No se activó la captura visual porque la sesión o la ventana seleccionada cambió.";
            return;
        }

        _visualCaptureAuthorization = null;
        var timelineContext = _coordinator?.ActiveSessionTimelineContext;
        var analysisAuthorization = _anonymousVisualAnalysisAuthorization;
        var activation = AnonymousVisualAnalysisActivator.TryCreate(
            _settings.CaptureSystemOutput,
            selection,
            sessionIdText,
            timelineContext,
            _store,
            analysisAuthorization,
            new AnonymousVisualAnalysisComponentFactory(
                OnAnonymousVisualAnalysisFailure,
                OnAnonymousVisualEvidencePersisted));
        if (analysisAuthorization is not null)
            _anonymousVisualAnalysisAuthorization = null;

        IVisualMeetingCapture capture;
        if (activation.IsActivated)
        {
            _anonymousVisualAnalysisSession = activation.Session;
            _anonymousVisualAnalysisContext = timelineContext;
            _anonymousVisualAnalysisProfileValidationState = activation.ProfileValidationState;
            _anonymousVisualAnalysisStatus = activation.ProfileValidationState == VisualProbeProfileValidationState.Validated
                ? AnonymousVisualAnalysisStatus.Running
                : AnonymousVisualAnalysisStatus.ProfileUnavailable;
            capture = new WindowsGraphicsCaptureService(selection, activation.Session);
        }
        else
        {
            if (analysisAuthorization is not null)
            {
                _anonymousVisualAnalysisStatus = activation.Failure ==
                    AnonymousVisualAnalysisActivationFailure.ComponentCreationFailed
                        ? AnonymousVisualAnalysisStatus.Failed
                        : AnonymousVisualAnalysisStatus.Off;
                StatusText.Text = AnonymousVisualAnalysisFailureMessage(activation.Failure);
            }
            capture = new WindowsGraphicsCaptureService(selection);
        }

        var observer = new WeakVisualCaptureStateObserver<MainWindow>(this);
        var controller = new VisualCaptureSessionController(
            sessionId,
            capture,
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
        var analysisSession = _anonymousVisualAnalysisSession;
        var analysisContext = _anonymousVisualAnalysisContext;
        var analysisSessionId = analysisContext?.SessionId;
        _visualCaptureController = null;
        _anonymousVisualAnalysisSession = null;
        _anonymousVisualAnalysisContext = null;
        _visualCaptureStateTracker.Reset(Guid.Empty);
        var visualCleanupFailed = false;
        if (controller is not null)
        {
            try
            {
                await controller.StopAsync();
                await controller.DisposeAsync();
            }
            catch
            {
                visualCleanupFailed = true;
            }
        }

        if (analysisSession is not null)
        {
            try
            {
                if (analysisContext?.TryGetCurrentOffset(out var finalOffset) == true)
                    await analysisSession.CompleteAt(finalOffset);
                await analysisSession.DisposeAsync();
                visualCleanupFailed |=
                    analysisSession.DetectorFailureCount > 0 ||
                    analysisSession.SinkFailureCount > 0 ||
                    analysisSession.ExtractionFailureCount > 0;
            }
            catch
            {
                visualCleanupFailed = true;
                try { await analysisSession.DisposeAsync(); } catch { }
            }
        }

        if (!string.IsNullOrWhiteSpace(analysisSessionId) && _anonymousVisualEvidenceProjector is not null)
        {
            _anonymousVisualEvidenceProjector.Invalidate(
                AnonymousVisualEvidenceCacheScope.ActiveSession,
                analysisSessionId);
            await RefreshActiveVisualEvidenceAsync(
                analysisSessionId,
                CancellationToken.None,
                allowAnalyzing: false);
            _anonymousVisualEvidenceProjector.Invalidate(
                AnonymousVisualEvidenceCacheScope.SelectedHistorySession,
                analysisSessionId);
            await RefreshSelectedHistoryVisualEvidenceAsync(
                analysisSessionId,
                CancellationToken.None,
                allowAnalyzing: false);
        }

        _anonymousVisualAnalysisProfileValidationState = null;
        _anonymousVisualAnalysisStatus = visualCleanupFailed || finalState == VisualCaptureState.Failed
            ? AnonymousVisualAnalysisStatus.Failed
            : finalState == VisualCaptureState.Off
                ? AnonymousVisualAnalysisStatus.Off
                : AnonymousVisualAnalysisStatus.Stopped;
        if (visualCleanupFailed && !_closing)
            StatusText.Text = "La parte visual informó un problema. El audio y la transcripción continúan.";
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

    private bool TryResolveAnonymousVisualAnalysisContext(
        MeetingWindowSelection? selection,
        out string sessionId,
        out ISessionTimelineContext timelineContext)
    {
        sessionId = string.Empty;
        timelineContext = null!;
        if (!_recording || _recordingPaused || !_settings.CaptureSystemOutput ||
            _visualCaptureController is not null || _visualCaptureState != VisualCaptureState.Off ||
            _anonymousVisualAnalysisAuthorization is not null ||
            _anonymousVisualAnalysisStatus != AnonymousVisualAnalysisStatus.Off ||
            _store is null || !IsCurrentVisualSelectionAvailable(selection) ||
            selection!.Provider is not (MeetingProvider.GoogleMeet or MeetingProvider.MicrosoftTeams))
            return false;

        var currentSessionId = _coordinator?.ActiveSessionId;
        var currentTimelineContext = _coordinator?.ActiveSessionTimelineContext;
        if (string.IsNullOrWhiteSpace(currentSessionId) ||
            currentTimelineContext is null ||
            !string.Equals(currentTimelineContext.SessionId, currentSessionId, StringComparison.Ordinal) ||
            !currentTimelineContext.TryGetCurrentOffset(out _))
            return false;

        sessionId = currentSessionId;
        timelineContext = currentTimelineContext;
        return true;
    }

    private void InvalidateVisualAuthorization()
    {
        _visualCaptureAuthorization = null;
        InvalidateAnonymousVisualAnalysisAuthorization();
    }

    private void InvalidateAnonymousVisualAnalysisAuthorization()
    {
        _anonymousVisualAnalysisAuthorization = null;
        if (_anonymousVisualAnalysisStatus == AnonymousVisualAnalysisStatus.Authorized)
            _anonymousVisualAnalysisStatus = AnonymousVisualAnalysisStatus.Off;
    }

    private void SetVisualCaptureState(VisualCaptureState state)
    {
        _visualCaptureState = state;
        UpdateAnonymousVisualAnalysisStateForCaptureState(state);
        UpdateVisualCaptureUi();
    }

    private void UpdateAnonymousVisualAnalysisStateForCaptureState(VisualCaptureState state)
    {
        if (_anonymousVisualAnalysisSession is null) return;
        if (_anonymousVisualAnalysisStatus == AnonymousVisualAnalysisStatus.Failed) return;

        if (_anonymousVisualAnalysisProfileValidationState != VisualProbeProfileValidationState.Validated)
        {
            _anonymousVisualAnalysisStatus = AnonymousVisualAnalysisStatus.ProfileUnavailable;
            return;
        }

        _anonymousVisualAnalysisStatus = state switch
        {
            VisualCaptureState.Active => AnonymousVisualAnalysisStatus.Running,
            VisualCaptureState.Pausing or VisualCaptureState.Paused or VisualCaptureState.TargetMinimized =>
                AnonymousVisualAnalysisStatus.Paused,
            VisualCaptureState.NotSupported or VisualCaptureState.TargetUnavailable or
                VisualCaptureState.ProtectedContent or VisualCaptureState.Stopped =>
                AnonymousVisualAnalysisStatus.Stopped,
            VisualCaptureState.Failed => AnonymousVisualAnalysisStatus.Failed,
            _ => _anonymousVisualAnalysisStatus
        };
    }

    private void OnAnonymousVisualAnalysisFailure(VisualProbeFailureKind failure)
    {
        if (Interlocked.Exchange(ref _anonymousVisualAnalysisFailureReported, 1) != 0) return;
        if (_closing || Dispatcher.HasShutdownStarted) return;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(() =>
        {
            if (_closing) return;
            _anonymousVisualAnalysisStatus = AnonymousVisualAnalysisStatus.Failed;
            StatusText.Text = failure switch
            {
                VisualProbeFailureKind.Extraction =>
                    "La evidencia visual no pudo extraerse. El audio y la transcripción continúan.",
                VisualProbeFailureKind.Detector =>
                    "La actividad visual anónima no pudo evaluarse. El audio y la transcripción continúan.",
                _ =>
                    "La evidencia visual cifrada no pudo guardarse. El audio y la transcripción continúan."
            };
            UpdateVisualCaptureUi();
        }));
    }

    private void OnAnonymousVisualEvidencePersisted(string sessionId)
    {
        if (_closing || _anonymousVisualEvidenceProjector is null) return;
        _anonymousVisualEvidenceProjector.Invalidate(
            AnonymousVisualEvidenceCacheScope.ActiveSession,
            sessionId);
        _anonymousVisualEvidenceProjector.Invalidate(
            AnonymousVisualEvidenceCacheScope.SelectedHistorySession,
            sessionId);
        QueueActiveVisualEvidenceRefresh(sessionId);
        QueueHistoryVisualEvidenceRefresh(sessionId);
    }

    private void QueueActiveVisualEvidenceRefresh(string sessionId)
    {
        if (_closing || Dispatcher.HasShutdownStarted) return;
        Interlocked.Exchange(ref _pendingActiveVisualEvidenceSessionId, sessionId);
        if (Interlocked.Exchange(ref _activeVisualEvidenceRefreshScheduled, 1) != 0) return;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(async () =>
        {
            Interlocked.Exchange(ref _activeVisualEvidenceRefreshScheduled, 0);
            var requestedSessionId = Interlocked.Exchange(
                ref _pendingActiveVisualEvidenceSessionId,
                null);
            if (_closing || requestedSessionId is null) return;
            try { await RefreshActiveVisualEvidenceAsync(requestedSessionId, _lifetime.Token); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        }));
    }

    private void QueueHistoryVisualEvidenceRefresh(string sessionId)
    {
        if (_closing || Dispatcher.HasShutdownStarted) return;
        Interlocked.Exchange(ref _pendingHistoryVisualEvidenceSessionId, sessionId);
        if (Interlocked.Exchange(ref _historyVisualEvidenceRefreshScheduled, 1) != 0) return;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(async () =>
        {
            Interlocked.Exchange(ref _historyVisualEvidenceRefreshScheduled, 0);
            var requestedSessionId = Interlocked.Exchange(
                ref _pendingHistoryVisualEvidenceSessionId,
                null);
            if (_closing || requestedSessionId is null) return;
            try { await RefreshSelectedHistoryVisualEvidenceAsync(requestedSessionId, _lifetime.Token); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        }));
    }

    private async Task RefreshActiveVisualEvidenceAsync(
        string sessionId,
        CancellationToken cancellationToken,
        bool allowAnalyzing = true)
    {
        var projector = _anonymousVisualEvidenceProjector;
        if (projector is null) return;
        var rows = _liveRows
            .Where(row =>
                row.Segment.Source == AudioSourceKind.SystemOutput &&
                string.Equals(row.Segment.SessionId, sessionId, StringComparison.Ordinal))
            .ToArray();
        if (rows.Length == 0) return;

        var projection = await projector.ProjectAsync(
            AnonymousVisualEvidenceCacheScope.ActiveSession,
            sessionId,
            rows.Select(row => row.Segment).ToArray(),
            validatedRunIncomplete:
                allowAnalyzing &&
                _anonymousVisualAnalysisProfileValidationState == VisualProbeProfileValidationState.Validated &&
                _anonymousVisualAnalysisStatus == AnonymousVisualAnalysisStatus.Running,
            cancellationToken: cancellationToken);
        if (!projection.IsCurrent || !projector.IsCurrent(projection)) return;
        if (!string.Equals(_coordinator?.ActiveSessionId, sessionId, StringComparison.Ordinal) && _recording) return;

        foreach (var row in rows)
        {
            var current = _liveRows.FirstOrDefault(candidate =>
                string.Equals(candidate.Segment.SessionId, sessionId, StringComparison.Ordinal) &&
                string.Equals(candidate.Segment.Id, row.Segment.Id, StringComparison.Ordinal));
            current?.SetVisualEvidence(projection.For(row.Segment));
        }
    }

    private async Task RefreshSelectedHistoryVisualEvidenceAsync(
        string sessionId,
        CancellationToken cancellationToken,
        bool allowAnalyzing = true)
    {
        var projector = _anonymousVisualEvidenceProjector;
        var selectedSession = SelectedHistorySession();
        if (projector is null ||
            selectedSession is null ||
            !string.Equals(selectedSession.Id, sessionId, StringComparison.Ordinal)) return;
        HistoryLoadTicket historyTicket;
        try { historyTicket = _historyLoads.Capture(sessionId); }
        catch (OperationCanceledException) { return; }
        var selectedRevisionId = SelectedHistoryRevisionId();
        var selectedSource = SelectedHistorySource();
        var rows = _historyRows
            .Where(row => string.Equals(row.Segment.SessionId, sessionId, StringComparison.Ordinal))
            .ToArray();
        if (rows.Length == 0) return;

        var projection = await projector.ProjectAsync(
            AnonymousVisualEvidenceCacheScope.SelectedHistorySession,
            sessionId,
            rows.Select(row => row.Segment).ToArray(),
            validatedRunIncomplete:
                allowAnalyzing &&
                string.Equals(_coordinator?.ActiveSessionId, sessionId, StringComparison.Ordinal) &&
                _anonymousVisualAnalysisProfileValidationState == VisualProbeProfileValidationState.Validated &&
                _anonymousVisualAnalysisStatus == AnonymousVisualAnalysisStatus.Running,
            cancellationToken: cancellationToken);
        if (!projection.IsCurrent || !projector.IsCurrent(projection) ||
            !_historyLoads.IsCurrent(historyTicket, SelectedHistorySession()?.Id) ||
            SelectedHistorySource() != selectedSource ||
            !string.Equals(SelectedHistoryRevisionId(), selectedRevisionId, StringComparison.Ordinal)) return;

        foreach (var row in rows)
        {
            var current = _historyRows.FirstOrDefault(candidate =>
                string.Equals(candidate.Segment.SessionId, sessionId, StringComparison.Ordinal) &&
                string.Equals(candidate.Segment.Id, row.Segment.Id, StringComparison.Ordinal));
            current?.SetVisualEvidence(projection.For(row.Segment));
        }
    }

    private static string AnonymousVisualAnalysisFailureMessage(
        AnonymousVisualAnalysisActivationFailure failure) => failure switch
    {
        AnonymousVisualAnalysisActivationFailure.SystemOutputRequired =>
            "El análisis anónimo no se inició porque esta sesión no captura audio del equipo.",
        AnonymousVisualAnalysisActivationFailure.ProviderUnsupported =>
            "El análisis anónimo solo se admite para Google Meet o Microsoft Teams.",
        AnonymousVisualAnalysisActivationFailure.TimelineMismatch =>
            "El análisis anónimo no se inició porque la sesión cambió.",
        AnonymousVisualAnalysisActivationFailure.StoreUnavailable or
            AnonymousVisualAnalysisActivationFailure.ComponentCreationFailed =>
            "La evidencia visual no está disponible por un problema local. El audio y la transcripción continúan.",
        _ =>
            "El análisis anónimo no se inició porque su autorización ya no es válida."
    };

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

        var anonymousState = AnonymousVisualAnalysisPresenter.Create(
            _anonymousVisualAnalysisStatus,
            hasValidSelection,
            selection?.Provider ?? MeetingProvider.NotSelected,
            _recording,
            _settings.CaptureSystemOutput,
            _visualCaptureState,
            _initialized && !_busy && !_closing,
            _recordingPaused,
            _visualCaptureActionInProgress);
        if (!string.Equals(AnonymousVisualAnalysisStatusText.Text, anonymousState.Status, StringComparison.Ordinal))
            AnonymousVisualAnalysisStatusText.Text = anonymousState.Status;
        AuthorizeAnonymousVisualAnalysisButton.IsEnabled = anonymousState.CanAuthorize;
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
        if (BlockCorrectionDraftNavigation()) return;
        try
        {
            await RefreshHistoryAsync();
            if (ReferenceEquals(HistoryWorkspaceTabs.SelectedItem, PendingReviewTabItem))
                await LoadPendingReviewsAsync();
        }
        catch (Exception ex) { ShowError("No se pudieron actualizar las sesiones", ex.Message); }
    }

    private async void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || !ReferenceEquals(e.OriginalSource, MainTabs)) return;

        if (e.RemovedItems.Contains(GlossaryTabItem)) await ClearGlossaryWorkspaceAsync();

        if (!ReferenceEquals(MainTabs.SelectedItem, HistoryTabItem))
        {
            await CancelHistoryNavigationAsync();
            await CancelPendingReviewLoadAsync();
            _playbackSpeedChanges.InvalidatePlaybackIntent();
            if (_historyPlaying || _activePlaybackOperation is not null)
            {
                await SupersedePlaybackAsync();
                StatusText.Text = "La reproducción se detuvo porque se cerró el Historial";
            }
        }

        if (ReferenceEquals(MainTabs.SelectedItem, GlossaryTabItem) && !_closing)
            await LoadGlossaryWorkspaceAsync();
    }

    private async void RefreshGlossary_Click(object sender, RoutedEventArgs e) =>
        await LoadGlossaryWorkspaceAsync();

    private async void ImportGlossary_Click(object sender, RoutedEventArgs e)
    {
        if (_store is null || _closing || !ReferenceEquals(MainTabs.SelectedItem, GlossaryTabItem)) return;
        var picker = new OpenFileDialog
        {
            Title = "Seleccionar diccionario JSON",
            Filter = "Diccionario Trazio (*.json)|*.json|Todos los archivos (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (picker.ShowDialog(this) != true) return;

        GlossaryImportPreview? preview = null;
        if (!_glossaryWorkspaceOperation.TryBegin([_lifetime.Token], out var previewOperation))
        {
            GlossaryWorkspaceStatusText.Text = "Espera a que termine la operación actual del diccionario.";
            return;
        }
        UpdateGlossaryWorkspaceControls();
        try
        {
            await using var stream = new FileStream(
                picker.FileName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81_920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var parsed = await GlossaryExchangeSerializer.ParseAsync(stream, previewOperation.CancellationToken);
            var existing = await _store.ListGlossaryAsync(previewOperation.CancellationToken);
            if (!CanPublishGlossaryWorkspace(previewOperation)) return;
            preview = GlossaryExchangePlanner.CreatePreview(existing, parsed);
        }
        catch (OperationCanceledException) when (previewOperation.CancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (CanPublishGlossaryWorkspace(previewOperation))
            {
                GlossaryWorkspaceStatusText.Text = "No se pudo preparar la vista previa; no se guardó ninguna entrada.";
                ShowError("No se pudo leer el diccionario", exception.Message);
            }
        }
        finally
        {
            _glossaryWorkspaceOperation.Complete(previewOperation);
            if (ReferenceEquals(MainTabs.SelectedItem, GlossaryTabItem)) UpdateGlossaryWorkspaceControls();
        }

        if (preview is null || _closing || !ReferenceEquals(MainTabs.SelectedItem, GlossaryTabItem)) return;
        var dialog = new GlossaryImportPreviewWindow(GlossaryImportPreviewPresenter.Create(preview)) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            GlossaryWorkspaceStatusText.Text = "Importación cancelada; la vista previa no cambió el diccionario.";
            return;
        }

        if (!_glossaryWorkspaceOperation.TryBegin([_lifetime.Token], out var applyOperation))
        {
            GlossaryWorkspaceStatusText.Text = "Espera a que termine la operación actual del diccionario y vuelve a revisar el archivo.";
            return;
        }

        var importCommitted = false;
        var reloadAfterSnapshotChange = false;
        UpdateGlossaryWorkspaceControls();
        try
        {
            var imported = await _store.ImportGlossaryEntriesAsync(
                preview.NewEntries,
                preview.ExistingSnapshotToken,
                Guid.NewGuid().ToString("N"),
                applyOperation.CancellationToken);
            importCommitted = true;
            var refreshed = await _store.ListGlossaryAsync(applyOperation.CancellationToken);
            if (!CanPublishGlossaryWorkspace(applyOperation)) return;
            _glossaryWorkspaceEntries = refreshed;
            ApplyGlossaryWorkspaceFilter($"Se importaron {imported.Count} entradas nuevas; las demás filas se omitieron según la vista previa.");
            StatusText.Text = $"Diccionario importado: {imported.Count} entradas nuevas";
        }
        catch (GlossaryImportSnapshotChangedException exception)
        {
            if (!CanPublishGlossaryWorkspace(applyOperation)) return;
            reloadAfterSnapshotChange = true;
            GlossaryWorkspaceStatusText.Text = "El diccionario cambió. No se importó nada; crea una vista previa nueva.";
            ShowError("La vista previa quedó obsoleta", exception.Message);
        }
        catch (OperationCanceledException) when (applyOperation.CancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!CanPublishGlossaryWorkspace(applyOperation)) return;
            if (importCommitted)
                FailGlossaryWorkspace("Las entradas se guardaron, pero no se pudo recargar la lista. Pulsa Actualizar.");
            else
                GlossaryWorkspaceStatusText.Text = "No se importó ninguna entrada.";
            ShowError("No se pudo importar el diccionario", exception.Message);
        }
        finally
        {
            _glossaryWorkspaceOperation.Complete(applyOperation);
            if (ReferenceEquals(MainTabs.SelectedItem, GlossaryTabItem)) UpdateGlossaryWorkspaceControls();
        }
        if (reloadAfterSnapshotChange && ReferenceEquals(MainTabs.SelectedItem, GlossaryTabItem))
            await LoadGlossaryWorkspaceAsync();
    }

    private async void ExportGlossary_Click(object sender, RoutedEventArgs e)
    {
        if (_store is null || _closing || !ReferenceEquals(MainTabs.SelectedItem, GlossaryTabItem)) return;
        if (MessageBox.Show(
                this,
                "Se exportarán todas las entradas a un archivo JSON sin cifrar. El archivo puede revelar términos privados. ¿Deseas continuar?",
                "Exportación sin cifrar",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        var picker = new SaveFileDialog
        {
            Title = "Exportar diccionario JSON",
            Filter = "Diccionario Trazio (*.json)|*.json",
            AddExtension = true,
            DefaultExt = ".json",
            FileName = $"trazio-diccionario-{DateTimeOffset.Now:yyyyMMdd-HHmm}.json",
            OverwritePrompt = true
        };
        if (picker.ShowDialog(this) != true) return;
        if (!_glossaryWorkspaceOperation.TryBegin([_lifetime.Token], out var operation))
        {
            GlossaryWorkspaceStatusText.Text = "Espera a que termine la operación actual del diccionario.";
            return;
        }

        UpdateGlossaryWorkspaceControls();
        try
        {
            var allEntries = await _store.ListGlossaryAsync(operation.CancellationToken);
            var json = GlossaryExchangeSerializer.Serialize(allEntries);
            await GlossaryExchangeFileWriter.WriteAtomicallyAsync(picker.FileName, json, operation.CancellationToken);
            if (!CanPublishGlossaryWorkspace(operation)) return;
            GlossaryWorkspaceStatusText.Text = $"Se exportaron las {allEntries.Count} entradas del diccionario a la ubicación elegida.";
            StatusText.Text = "Diccionario JSON exportado sin cifrar";
        }
        catch (OperationCanceledException) when (operation.CancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!CanPublishGlossaryWorkspace(operation)) return;
            GlossaryWorkspaceStatusText.Text = "No se completó la exportación; el destino anterior se conservó.";
            ShowError("No se pudo exportar el diccionario", exception.Message);
        }
        finally
        {
            _glossaryWorkspaceOperation.Complete(operation);
            if (ReferenceEquals(MainTabs.SelectedItem, GlossaryTabItem)) UpdateGlossaryWorkspaceControls();
        }
    }

    private void GlossaryFilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_settingGlossaryFilters || _glossaryWorkspaceLoadFailed || GlossaryWorkspaceList is null || _glossaryWorkspaceOperation.IsRunning) return;
        ApplyGlossaryWorkspaceFilter();
        UpdateGlossaryWorkspaceControls();
    }

    private void GlossaryActivityFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settingGlossaryFilters || _glossaryWorkspaceLoadFailed || GlossaryWorkspaceList is null || _glossaryWorkspaceOperation.IsRunning) return;
        ApplyGlossaryWorkspaceFilter();
        UpdateGlossaryWorkspaceControls();
    }

    private void ClearGlossaryFilter_Click(object sender, RoutedEventArgs e)
    {
        _settingGlossaryFilters = true;
        try
        {
            GlossaryFilterBox.Clear();
            GlossaryActivityFilter.SelectedIndex = 0;
        }
        finally { _settingGlossaryFilters = false; }
        ApplyGlossaryWorkspaceFilter();
        UpdateGlossaryWorkspaceControls();
        GlossaryFilterBox.Focus();
    }

    private async void GlossaryEntryActive_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkBox ||
            checkBox.DataContext is not GlossaryWorkspaceItem item ||
            _store is null ||
            !ReferenceEquals(MainTabs.SelectedItem, GlossaryTabItem))
            return;

        var requestedActivity = checkBox.IsChecked == true;
        checkBox.IsChecked = item.IsActive;
        if (!_glossaryWorkspaceOperation.TryBegin([_lifetime.Token], out var operation))
        {
            GlossaryWorkspaceStatusText.Text = "Espera a que termine la operación actual del diccionario.";
            return;
        }

        var writeConfirmed = false;
        var updateCompleted = false;
        UpdateGlossaryWorkspaceControls();
        try
        {
            var updated = await _store.SetGlossaryEntryActiveAsync(
                item.EntryId,
                requestedActivity,
                operation.CancellationToken);
            writeConfirmed = updated;
            updateCompleted = true;
            var refreshed = await _store.ListGlossaryAsync(operation.CancellationToken);
            if (!CanPublishGlossaryWorkspace(operation)) return;

            _glossaryWorkspaceEntries = refreshed;
            ApplyGlossaryWorkspaceFilter(updated
                ? $"La entrada quedó {(requestedActivity ? "activa" : "inactiva")}."
                : "La entrada ya no existía; se volvió a cargar el diccionario.");
            StatusText.Text = updated
                ? $"Entrada del diccionario {(requestedActivity ? "activada" : "desactivada")}"
                : "La entrada del diccionario ya no existe";
        }
        catch (OperationCanceledException) when (operation.CancellationToken.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!CanPublishGlossaryWorkspace(operation)) return;
            if (updateCompleted)
            {
                FailGlossaryWorkspace(writeConfirmed
                    ? "El cambio fue guardado, pero no se pudo volver a cargar el diccionario. Pulsa Actualizar."
                    : "No se pudo volver a cargar el diccionario después de comprobar la entrada. Pulsa Actualizar.");
            }
            else
            {
                ApplyGlossaryWorkspaceFilter("No se pudo cambiar el estado; se restauró el valor anterior.");
            }
            StatusText.Text = "No se pudo cambiar la entrada del diccionario.";
        }
        finally
        {
            _glossaryWorkspaceOperation.Complete(operation);
            if (ReferenceEquals(MainTabs.SelectedItem, GlossaryTabItem)) UpdateGlossaryWorkspaceControls();
        }
    }

    private async Task LoadGlossaryWorkspaceAsync()
    {
        if (!_initialized || _store is null || _closing || !ReferenceEquals(MainTabs.SelectedItem, GlossaryTabItem)) return;
        if (!_glossaryWorkspaceOperation.TryBegin([_lifetime.Token], out var operation)) return;

        _glossaryWorkspaceLoadFailed = false;
        _glossaryWorkspaceEntries = [];
        _glossaryWorkspaceRows.Clear();
        GlossaryWorkspaceList.Visibility = Visibility.Collapsed;
        GlossaryWorkspaceEmptyText.Text = GlossaryWorkspacePresenter.LoadingStatus;
        GlossaryWorkspaceEmptyText.Visibility = Visibility.Visible;
        GlossaryWorkspaceStatusText.Text = GlossaryWorkspacePresenter.LoadingStatus;
        UpdateGlossaryWorkspaceControls();
        try
        {
            var entries = await _store.ListGlossaryAsync(operation.CancellationToken);
            if (!CanPublishGlossaryWorkspace(operation)) return;
            _glossaryWorkspaceLoadFailed = false;
            _glossaryWorkspaceEntries = entries;
            ApplyGlossaryWorkspaceFilter();
            StatusText.Text = "Diccionario actualizado";
        }
        catch (OperationCanceledException) when (operation.CancellationToken.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!CanPublishGlossaryWorkspace(operation)) return;
            FailGlossaryWorkspace("Comprueba el almacenamiento cifrado e intenta nuevamente.");
            StatusText.Text = "No se pudo cargar el diccionario.";
        }
        finally
        {
            _glossaryWorkspaceOperation.Complete(operation);
            if (ReferenceEquals(MainTabs.SelectedItem, GlossaryTabItem)) UpdateGlossaryWorkspaceControls();
        }
    }

    private async Task ClearGlossaryWorkspaceAsync()
    {
        await _glossaryWorkspaceOperation.CancelAndWaitAsync();
        _glossaryWorkspaceLoadFailed = false;
        _glossaryWorkspaceEntries = [];
        _glossaryWorkspaceRows.Clear();
        _settingGlossaryFilters = true;
        try
        {
            GlossaryFilterBox.Clear();
            GlossaryActivityFilter.SelectedIndex = 0;
        }
        finally { _settingGlossaryFilters = false; }
        GlossaryWorkspaceList.Visibility = Visibility.Collapsed;
        GlossaryWorkspaceEmptyText.Visibility = Visibility.Collapsed;
        GlossaryWorkspaceStatusText.Text = "Abre esta pestaña para cargar el diccionario.";
        UpdateGlossaryWorkspaceControls();
    }

    private bool CanPublishGlossaryWorkspace(OwnedCancellationOperationCoordinator.Operation operation) =>
        _glossaryWorkspaceOperation.IsCurrent(operation) &&
        !operation.CancellationToken.IsCancellationRequested &&
        !_closing &&
        ReferenceEquals(MainTabs.SelectedItem, GlossaryTabItem);

    private void ApplyGlossaryWorkspaceFilter(string? announcement = null)
    {
        if (GlossaryWorkspaceList is null) return;
        var state = GlossaryWorkspacePresenter.Create(
            _glossaryWorkspaceEntries,
            GlossaryFilterBox.Text,
            SelectedGlossaryActivityFilter());
        _glossaryWorkspaceRows.Clear();
        foreach (var item in state.Items) _glossaryWorkspaceRows.Add(item);
        GlossaryWorkspaceStatusText.Text = string.IsNullOrWhiteSpace(announcement)
            ? state.Status
            : $"{state.Status} {announcement}";
        GlossaryWorkspaceEmptyText.Text = state.TotalCount == 0
            ? "El diccionario todavía no tiene entradas."
            : "No hay coincidencias para el filtro actual.";
        GlossaryWorkspaceList.Visibility = state.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        GlossaryWorkspaceEmptyText.Visibility = state.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private GlossaryEntryActivityFilter SelectedGlossaryActivityFilter() =>
        (GlossaryActivityFilter.SelectedItem as ComboBoxItem)?.Tag?.ToString() switch
        {
            "Active" => GlossaryEntryActivityFilter.Active,
            "Inactive" => GlossaryEntryActivityFilter.Inactive,
            _ => GlossaryEntryActivityFilter.All
        };

    private void FailGlossaryWorkspace(string guidance)
    {
        _glossaryWorkspaceLoadFailed = true;
        _glossaryWorkspaceEntries = [];
        _glossaryWorkspaceRows.Clear();
        GlossaryWorkspaceList.Visibility = Visibility.Collapsed;
        GlossaryWorkspaceEmptyText.Text = GlossaryWorkspacePresenter.ErrorStatus;
        GlossaryWorkspaceEmptyText.Visibility = Visibility.Visible;
        GlossaryWorkspaceStatusText.Text = $"{GlossaryWorkspacePresenter.ErrorStatus} {guidance}";
    }

    private void UpdateGlossaryWorkspaceControls()
    {
        if (GlossaryWorkspaceList is null) return;
        var canInteract = _initialized &&
            _store is not null &&
            !_closing &&
            ReferenceEquals(MainTabs.SelectedItem, GlossaryTabItem) &&
            !_glossaryWorkspaceOperation.IsRunning;
        RefreshGlossaryButton.IsEnabled = canInteract;
        ImportGlossaryButton.IsEnabled = canInteract;
        ExportGlossaryButton.IsEnabled = canInteract && !_glossaryWorkspaceLoadFailed;
        var canBrowse = canInteract && !_glossaryWorkspaceLoadFailed;
        GlossaryFilterBox.IsEnabled = canBrowse;
        GlossaryActivityFilter.IsEnabled = canBrowse;
        GlossaryWorkspaceList.IsEnabled = canBrowse;
        ClearGlossaryFilterButton.IsEnabled = canBrowse &&
            (!string.IsNullOrWhiteSpace(GlossaryFilterBox.Text) || GlossaryActivityFilter.SelectedIndex > 0);
    }

    private async void RefreshHistory_Click(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _store is null) { StatusText.Text = "El historial todavía se está cargando"; return; }
        if (BlockCorrectionDraftNavigation()) return;
        try
        {
            await RefreshHistoryAsync();
            if (ReferenceEquals(HistoryWorkspaceTabs.SelectedItem, PendingReviewTabItem))
                await LoadPendingReviewsAsync();
            StatusText.Text = "Sesiones guardadas actualizadas";
        }
        catch (Exception ex) { ShowError("No se pudieron actualizar las sesiones", ex.Message); }
    }

    private async void HistoryWorkspaceTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, HistoryWorkspaceTabs) || !_initialized || _closing) return;
        if (ReferenceEquals(HistoryWorkspaceTabs.SelectedItem, PendingReviewTabItem))
            await LoadPendingReviewsAsync();
        else
        {
            await CancelHistoryNavigationAsync();
            await CancelPendingReviewLoadAsync();
        }
    }

    private async void RefreshPendingReviews_Click(object sender, RoutedEventArgs e) =>
        await LoadPendingReviewsAsync();

    private async Task LoadPendingReviewsAsync()
    {
        if (!_initialized || _store is null || _closing || _historyDeleteInProgress) return;

        await CancelHistoryNavigationAsync();

        PendingReviewTicket ticket;
        OwnedCancellationOperationCoordinator.Operation operation;
        await _pendingReviewTransition.WaitAsync(_lifetime.Token);
        try
        {
            _pendingReviewLoads.Invalidate();
            await _pendingReviewOperation.CancelAndWaitAsync();
            if (_closing || _historyDeleteInProgress) return;
            ticket = _pendingReviewLoads.Begin();
            if (!_pendingReviewOperation.TryBegin(
                    [_lifetime.Token, ticket.CancellationToken],
                    out operation))
                return;
        }
        finally
        {
            _pendingReviewTransition.Release();
        }

        PendingReviewStatusText.Text = PendingReviewPresenter.LoadingStatus;
        PendingReviewEmptyText.Visibility = Visibility.Collapsed;
        _suppressPendingReviewSelectionChanged = true;
        try { _pendingReviewRows.Clear(); }
        finally { _suppressPendingReviewSelectionChanged = false; }
        UpdatePendingReviewControls();

        try
        {
            var result = await PendingReviewQueryDispatcher.RunAsync(
                cancellationToken => _store.ListPendingSegmentReviewsAsync(
                    PendingSegmentReviewLimits.MaximumVisibleItems,
                    cancellationToken),
                operation.CancellationToken);
            if (!IsCurrentPendingReviewLoad(ticket, operation)) return;

            var presentation = PendingReviewPresenter.Create(result);
            _suppressPendingReviewSelectionChanged = true;
            try
            {
                _pendingReviewRows.Clear();
                foreach (var item in presentation.Items) _pendingReviewRows.Add(item);
            }
            finally { _suppressPendingReviewSelectionChanged = false; }
            PendingReviewStatusText.Text = presentation.Status;
            PendingReviewEmptyText.Visibility = presentation.Items.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        catch (OperationCanceledException) when (!IsCurrentPendingReviewLoad(ticket, operation)) { }
        catch (Exception)
        {
            if (!IsCurrentPendingReviewLoad(ticket, operation)) return;
            _suppressPendingReviewSelectionChanged = true;
            try { _pendingReviewRows.Clear(); }
            finally { _suppressPendingReviewSelectionChanged = false; }
            PendingReviewEmptyText.Visibility = Visibility.Collapsed;
            PendingReviewStatusText.Text = PendingReviewPresenter.ErrorStatus;
            StatusText.Text = PendingReviewPresenter.ErrorStatus;
        }
        finally
        {
            if (_pendingReviewOperation.Complete(operation)) UpdatePendingReviewControls();
        }
    }

    private bool IsCurrentPendingReviewLoad(
        PendingReviewTicket ticket,
        OwnedCancellationOperationCoordinator.Operation operation) =>
        _pendingReviewLoads.IsCurrent(ticket) &&
        _pendingReviewOperation.IsCurrent(operation) &&
        !_closing &&
        !_historyDeleteInProgress;

    private async Task CancelPendingReviewLoadAsync()
    {
        await _pendingReviewTransition.WaitAsync();
        try
        {
            _pendingReviewLoads.Invalidate();
            await _pendingReviewOperation.CancelAndWaitAsync();
        }
        finally { _pendingReviewTransition.Release(); }
        UpdatePendingReviewControls();
    }

    private void PendingReviewBatchCheckbox_PreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (sender is not CheckBox checkBox || !checkBox.IsEnabled) return;
        checkBox.IsChecked = checkBox.IsChecked != true;
        e.Handled = true;
    }

    private void PendingReviewBatchSelectionChanged(object sender, RoutedEventArgs e) =>
        UpdatePendingReviewControls();

    private async void ApproveSelectedPendingReviews_Click(object sender, RoutedEventArgs e) =>
        await ApproveSelectedPendingReviewsAsync();

    private async Task ApproveSelectedPendingReviewsAsync()
    {
        if (_store is null || !_initialized || _closing || _historyDeleteInProgress ||
            _pendingReviewOperation.IsRunning || HasUnsavedCorrectionDraft())
            return;

        var selectedRows = _pendingReviewRows.Where(item => item.IsBatchSelected).ToArray();
        var selection = PendingReviewBatchSelectionPresenter.Create(selectedRows, canInteract: true);
        if (!selection.CanApprove) return;

        var preview = string.Join(
            Environment.NewLine,
            selectedRows.Take(5).Select(item => $"• {item.MeetingTitle} · {item.Details}{Environment.NewLine}  {item.Snippet}"));
        if (selectedRows.Length > 5)
            preview += $"{Environment.NewLine}• … y {selectedRows.Length - 5} segmento(s) más";
        var confirmation = MessageBox.Show(
            this,
            $"Se marcarán {selection.SelectedCount} texto(s) original(es) como revisado(s):{Environment.NewLine}{Environment.NewLine}" +
            preview +
            $"{Environment.NewLine}{Environment.NewLine}Esta acción no corrige texto ni modifica audio. " +
            "La operación es atómica: si un segmento cambió, no se aprobará ninguno. " +
            "Después puedes reabrir cada segmento individualmente.",
            "Confirmar aprobación múltiple",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirmation != MessageBoxResult.Yes) return;

        var reviewer = string.IsNullOrWhiteSpace(_settings.LocalDisplayName)
            ? Environment.UserName
            : _settings.LocalDisplayName;
        if (string.IsNullOrWhiteSpace(reviewer)) reviewer = "Usuario local";

        OwnedCancellationOperationCoordinator.Operation operation;
        await _pendingReviewTransition.WaitAsync(_lifetime.Token);
        try
        {
            _pendingReviewLoads.Invalidate();
            await _pendingReviewOperation.CancelAndWaitAsync();
            if (_closing || _historyDeleteInProgress) return;
            if (!_pendingReviewOperation.TryBegin([_lifetime.Token], out operation)) return;
        }
        finally
        {
            _pendingReviewTransition.Release();
        }

        PendingReviewStatusText.Text =
            $"Guardando la revisión de {selection.SelectedCount} segmento(s)…";
        UpdatePendingReviewControls();

        BatchSegmentReviewWriteResult? result = null;
        var canPublish = false;
        try
        {
            result = await PendingReviewQueryDispatcher.RunAsync(
                cancellationToken => _store.ApproveOriginalSegmentsAsync(
                    selection.Requests,
                    reviewer,
                    cancellationToken),
                operation.CancellationToken);
            canPublish = _pendingReviewOperation.IsCurrent(operation) &&
                !_closing &&
                !_historyDeleteInProgress;
        }
        catch (OperationCanceledException) when (!_pendingReviewOperation.IsCurrent(operation)) { }
        catch (Exception ex)
        {
            if (_pendingReviewOperation.IsCurrent(operation))
            {
                PendingReviewStatusText.Text =
                    "No se pudo guardar la aprobación múltiple. No se modificó ningún segmento.";
                ShowError("No se pudo guardar la revisión múltiple", ex.Message);
            }
        }
        finally
        {
            _pendingReviewOperation.Complete(operation);
            UpdatePendingReviewControls();
        }

        if (!canPublish || result is null) return;
        await LoadPendingReviewsAsync();
        if (result.Status == BatchSegmentReviewWriteStatus.Applied)
        {
            var message =
                $"{result.Decisions.Count} texto(s) original(es) marcado(s) como revisado(s); no se creó ninguna corrección.";
            PendingReviewStatusText.Text = message;
            StatusText.Text = message;
        }
        else
        {
            var message =
                $"{result.Conflicts.Count} segmento(s) cambiaron antes de confirmar. No se aprobó ninguno; la lista se actualizó.";
            PendingReviewStatusText.Text = message;
            StatusText.Text = message;
        }
    }
    private async void PendingReviewList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressPendingReviewSelectionChanged ||
            PendingReviewList.SelectedItem is not PendingReviewItem selected)
            return;
        if (!_correctionDraftNavigation.CanNavigate())
        {
            await CancelHistoryNavigationAsync();
            _suppressPendingReviewSelectionChanged = true;
            try { PendingReviewList.SelectedItem = null; }
            finally { _suppressPendingReviewSelectionChanged = false; }
            AnnounceBlockedCorrectionDraftNavigation();
            return;
        }
        if (_store is null ||
            !HistoryPlaybackAvailability.CanBeginOperation(_historyDeleteInProgress, _closing))
            return;

        var pendingIntent = PendingReviewNavigationIntent.From(selected);
        var intent = new HistorySearchNavigationIntent(
            pendingIntent.SessionId,
            pendingIntent.SegmentId,
            pendingIntent.Source,
            pendingIntent.UseOriginalRevision,
            pendingIntent.AutoPlay);
        var editorTicket = _correctionDraftNavigation.CaptureOperation();
        var navigation = await BeginHistoryNavigationAsync(intent);
        if (navigation is null) return;
        var (ticket, operation) = navigation.Value;
        if (!_historySearchActivity.TryBegin(out var lease))
        {
            if (IsCurrentHistorySearchNavigation(ticket)) _historySearchNavigation.Invalidate();
            _historyNavigationOperation.Complete(operation);
            return;
        }

        try
        {
            using (lease)
            {
                await OpenHistorySearchResultAsync(intent, ticket, editorTicket);
            }
            if (!IsCurrentHistorySearchNavigation(ticket) ||
                !string.Equals(SelectedHistorySession()?.Id, pendingIntent.SessionId, StringComparison.Ordinal) ||
                !string.Equals(SelectedHistorySegment()?.Segment.Id, pendingIntent.SegmentId, StringComparison.Ordinal))
                return;

            StatusText.Text = "Pendiente abierto en la transcripción original; el audio no se reprodujo automáticamente.";
            UpdateSegmentReviewStatus();
            HistorySegments.Focus();
        }
        finally { _historyNavigationOperation.Complete(operation); }
    }

    private async void SearchHistory_Click(object sender, RoutedEventArgs e) => await RunTrackedHistorySearchAsync();

    private async void HistorySearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await RunTrackedHistorySearchAsync();
    }

    private async void HistorySearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (HistorySearchResults is null || HistoryList is null) return;
        _historySearch.Invalidate();
        await CancelHistoryNavigationAsync();
        HideHistorySearchResults();
        HistorySearchStatusText.Text = HistorySearchPresenter.IdleStatus;
        UpdateHistorySearchControls();
    }

    private async void ClearHistorySearch_Click(object sender, RoutedEventArgs e)
    {
        _historySearch.Invalidate();
        await CancelHistoryNavigationAsync();
        HistorySearchBox.Clear();
        HideHistorySearchResults();
        HistorySearchStatusText.Text = HistorySearchPresenter.IdleStatus;
        UpdateHistorySearchControls();
        HistorySearchBox.Focus();
    }

    private async Task RunHistorySearchAsync()
    {
        if (!_initialized || _store is null || _closing || _historyDeleteInProgress) return;
        string query;
        try { query = HistorySearchText.PrepareQuery(HistorySearchBox.Text); }
        catch (ArgumentException ex)
        {
            HideHistorySearchResults();
            HistorySearchStatusText.Text = ex.Message;
            StatusText.Text = ex.Message;
            UpdateHistorySearchControls();
            return;
        }

        var ticket = _historySearch.Begin(query);
        HistorySearchStatusText.Text = "Buscando localmente en el historial cifrado…";
        StatusText.Text = "Buscando en reuniones guardadas";
        UpdateHistorySearchControls();
        try
        {
            var result = await Task.Run(
                () => _store.SearchHistoryAsync(
                    query,
                    HistorySearchText.MaximumResults,
                    ticket.CancellationToken),
                ticket.CancellationToken);
            if (!_historySearch.IsCurrent(ticket, HistorySearchBox.Text)) return;
            var presentation = HistorySearchPresenter.Create(result);
            _historySearchRows.Clear();
            foreach (var item in presentation.Items) _historySearchRows.Add(item);
            HistoryList.Visibility = Visibility.Collapsed;
            HistorySearchResults.Visibility = Visibility.Visible;
            HistorySearchStatusText.Text = presentation.Status;
            StatusText.Text = presentation.Status;
        }
        catch (OperationCanceledException) when (!_historySearch.IsCurrent(ticket, HistorySearchBox.Text)) { }
        catch (Exception ex)
        {
            if (!_historySearch.IsCurrent(ticket, HistorySearchBox.Text)) return;
            _historySearchRows.Clear();
            HistoryList.Visibility = Visibility.Collapsed;
            HistorySearchResults.Visibility = Visibility.Visible;
            HistorySearchStatusText.Text = "No se pudo completar la búsqueda. No se muestran resultados parciales.";
            ShowError("No se pudo buscar en las reuniones", ex.Message);
        }
        finally { UpdateHistorySearchControls(); }
    }

    private async Task RunTrackedHistorySearchAsync()
    {
        await CancelHistoryNavigationAsync();
        if (!_historySearchActivity.TryBegin(out var lease)) return;
        using (lease) await RunHistorySearchAsync();
    }

    private void HideHistorySearchResults()
    {
        _historySearchRows.Clear();
        HistorySearchResults.SelectedItem = null;
        HistorySearchResults.Visibility = Visibility.Collapsed;
        HistoryList.Visibility = Visibility.Visible;
    }

    private void UpdateHistorySearchControls()
    {
        if (SearchHistoryButton is null || ClearHistorySearchButton is null ||
            HistorySearchBox is null || HistorySearchResults is null)
            return;
        var hasQuery = !string.IsNullOrWhiteSpace(HistorySearchBox.Text);
        var canSearch = false;
        if (hasQuery)
        {
            try
            {
                HistorySearchText.PrepareQuery(HistorySearchBox.Text);
                canSearch = true;
            }
            catch (ArgumentException) { }
        }
        var interactionsAllowed = !_closing && !_historyDeleteInProgress && _historySearchActivity.IsAccepting;
        SearchHistoryButton.IsEnabled = _initialized && interactionsAllowed && canSearch;
        ClearHistorySearchButton.IsEnabled = interactionsAllowed &&
            (hasQuery || HistorySearchResults.Visibility == Visibility.Visible);
    }

    private async Task RefreshHistoryAsync(
        CancellationToken cancellationToken = default,
        bool suppressSelectionChanged = false)
    {
        if (_store is null) throw new InvalidOperationException("El almacenamiento del historial todavía no está listo.");
        var preserveCorrectionDraft = HasUnsavedCorrectionDraft();
        cancellationToken.ThrowIfCancellationRequested();
        var selectedId = SelectedHistorySession()?.Id;
        if (selectedId is not null)
            _anonymousVisualEvidenceProjector?.Invalidate(
                AnonymousVisualEvidenceCacheScope.SelectedHistorySession,
                selectedId);
        var sessions = (await _store.ListSessionsAsync(cancellationToken))
            .Select(session => HistorySessionItem.From(session))
            .ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        if (suppressSelectionChanged || preserveCorrectionDraft) _suppressHistoryListSelectionChanged = true;
        try
        {
            HistoryList.ItemsSource = sessions;
            HistoryList.SelectedItem = sessions.FirstOrDefault(item => item.Session.Id == selectedId);
        }
        finally
        {
            if (suppressSelectionChanged || preserveCorrectionDraft) _suppressHistoryListSelectionChanged = false;
        }
        if (HistoryList.SelectedItem is null && !preserveCorrectionDraft)
            ApplyHistoryState(HistoryPresenter.Create(false, null, [], [], SelectedHistorySource(), FormatTranscript));
        UpdateHistoryControls();
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
            _liveRows.Add(new(segment));
            LiveTranscript.ScrollIntoView(_liveRows.Last());
            if (segment.Source == AudioSourceKind.SystemOutput)
                QueueActiveVisualEvidenceRefresh(segment.SessionId);
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
        if (_suppressHistoryListSelectionChanged) return;
        if (RestoreCorrectionDraftSessionSelectionIfNeeded()) return;
        if (HasUnsavedCorrectionDraft()) return;
        var editorTicket = _correctionDraftNavigation.CaptureOperation();
        var session = SelectedHistorySession();
        await CancelHistoryNavigationAsync();
        if (!CanPublishCorrectionEditor(editorTicket)) return;
        await SelectHistorySessionAsync(session, editorTicket: editorTicket);
    }

    private async Task SelectHistorySessionAsync(
        SessionSummary? session,
        HistorySearchNavigationIntent? navigation = null,
        HistorySearchNavigationTicket? navigationTicket = null,
        CorrectionEditorOperationTicket? editorTicket = null)
    {
        if (navigationTicket is not null && !IsCurrentHistorySearchNavigation(navigationTicket)) return;
        CancelHistoryRetranscription();
        _historyRevisionLoads.Invalidate();
        await SupersedePlaybackAsync();
        if (navigationTicket is not null && !IsCurrentHistorySearchNavigation(navigationTicket)) return;
        if (!CanPublishCorrectionEditor(editorTicket)) return;
        if (!HistoryPlaybackAvailability.CanBeginOperation(_historyDeleteInProgress, _closing)) return;
        if (navigation?.Source is { } requestedSource)
        {
            _settingHistorySource = true;
            try
            {
                HistoryAudioSource.SelectedItem = HistoryAudioSource.Items.Cast<ComboBoxItem>()
                    .First(item => string.Equals(item.Tag?.ToString(), requestedSource.ToString(), StringComparison.Ordinal));
            }
            finally { _settingHistorySource = false; }
        }
        PopulateComparisonSelectors([]);
        UpdateSessionTitleEditor(session);
        if (session is null || _store is null)
        {
            if (!CanPublishCorrectionEditor(editorTicket)) return;
            _historyLoads.Invalidate();
            _anonymousVisualEvidenceProjector?.Clear(
                AnonymousVisualEvidenceCacheScope.SelectedHistorySession);
            ClearHistoryReview();
            ApplyHistoryState(HistoryPresenter.Create(false, null, [], [], SelectedHistorySource(), FormatTranscript));
            return;
        }

        var ticket = _historyLoads.Begin(session.Id, navigationTicket?.CancellationToken ?? default);
        try
        {
            var published = await LoadHistoryReviewAsync(
                session,
                ticket,
                navigation?.SegmentId,
                preserveRequestedSource: navigation?.Source is not null,
                editorTicket: editorTicket);
            if (navigationTicket is not null && !IsCurrentHistorySearchNavigation(navigationTicket)) return;
            if (!published) return;
            if (navigation?.SegmentId is { } segmentId)
            {
                var selected = SelectedHistorySegment();
                if (selected is null || !string.Equals(selected.Segment.Id, segmentId, StringComparison.Ordinal))
                    throw new InvalidOperationException("El fragmento encontrado ya no existe. Ejecuta la búsqueda nuevamente.");
                HistorySegments.ScrollIntoView(selected);
                StatusText.Text = "Resultado abierto en la transcripción original; el audio no se reprodujo automáticamente.";
            }
        }
        catch (OperationCanceledException) when (
            !_historyLoads.IsCurrent(ticket, SelectedHistorySession()?.Id) ||
            navigationTicket is not null && !IsCurrentHistorySearchNavigation(navigationTicket)) { }
        catch (Exception ex)
        {
            if (_historyLoads.IsCurrent(ticket, SelectedHistorySession()?.Id) &&
                (navigationTicket is null || IsCurrentHistorySearchNavigation(navigationTicket)))
                ShowError("No se pudo cargar la sesión", ex.Message);
        }
    }

    private async void HistorySearchResults_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressHistorySearchResultSelectionChanged) return;
        if (HasUnsavedCorrectionDraft() && HistorySearchResults.SelectedItem is not null)
        {
            await CancelHistoryNavigationAsync();
            _suppressHistorySearchResultSelectionChanged = true;
            try { HistorySearchResults.SelectedItem = null; }
            finally { _suppressHistorySearchResultSelectionChanged = false; }
            AnnounceBlockedCorrectionDraftNavigation();
            return;
        }
        if (HistorySearchResults.SelectedItem is not HistorySearchResultItem selected)
        {
            await CancelHistoryNavigationAsync();
            return;
        }
        if (_store is null ||
            !HistoryPlaybackAvailability.CanBeginOperation(_historyDeleteInProgress, _closing))
            return;

        var intent = HistorySearchNavigationIntent.From(selected.Hit);
        var editorTicket = _correctionDraftNavigation.CaptureOperation();
        var navigation = await BeginHistoryNavigationAsync(intent);
        if (navigation is null) return;
        var (ticket, operation) = navigation.Value;
        if (!_historySearchActivity.TryBegin(out var lease))
        {
            if (IsCurrentHistorySearchNavigation(ticket)) _historySearchNavigation.Invalidate();
            _historyNavigationOperation.Complete(operation);
            return;
        }

        try
        {
            using (lease)
            {
                await OpenHistorySearchResultAsync(intent, ticket, editorTicket);
            }
        }
        finally { _historyNavigationOperation.Complete(operation); }
    }

    private async Task OpenHistorySearchResultAsync(
        HistorySearchNavigationIntent intent,
        HistorySearchNavigationTicket ticket,
        CorrectionEditorOperationTicket editorTicket)
    {
        try
        {
            if (_store is null || !IsCurrentHistorySearchNavigation(ticket)) return;
            var storedSessions = await _store.ListSessionsAsync(ticket.CancellationToken);
            if (!IsCurrentHistorySearchNavigation(ticket)) return;
            if (!CanPublishCorrectionEditor(editorTicket)) return;
            var storedSession = storedSessions
                .FirstOrDefault(session => string.Equals(session.Id, intent.SessionId, StringComparison.Ordinal));
            if (storedSession is null)
                throw new InvalidOperationException("La reunión encontrada ya no existe. Ejecuta la búsqueda nuevamente.");

            var item = HistoryList.Items.OfType<HistorySessionItem>()
                .FirstOrDefault(candidate => string.Equals(candidate.Session.Id, intent.SessionId, StringComparison.Ordinal));
            if (item is null)
            {
                await RefreshHistoryAsync(ticket.CancellationToken, suppressSelectionChanged: true);
                if (!IsCurrentHistorySearchNavigation(ticket)) return;
                if (!CanPublishCorrectionEditor(editorTicket)) return;
                item = HistoryList.Items.OfType<HistorySessionItem>()
                    .FirstOrDefault(candidate => string.Equals(candidate.Session.Id, intent.SessionId, StringComparison.Ordinal));
            }
            if (item is null)
                throw new InvalidOperationException("La reunión encontrada ya no está disponible en el historial.");

            if (!ReferenceEquals(HistoryList.SelectedItem, item))
            {
                if (!IsCurrentHistorySearchNavigation(ticket)) return;
                _suppressHistoryListSelectionChanged = true;
                try { HistoryList.SelectedItem = item; }
                finally { _suppressHistoryListSelectionChanged = false; }
                if (!IsCurrentHistorySearchNavigation(ticket)) return;
            }
            await SelectHistorySessionAsync(storedSession, intent, ticket, editorTicket);
            if (!IsCurrentHistorySearchNavigation(ticket)) return;
            _historyLoads.Begin(storedSession.Id);
            HistoryList.ScrollIntoView(item);
        }
        catch (OperationCanceledException) when (!IsCurrentHistorySearchNavigation(ticket)) { }
        catch (Exception ex)
        {
            if (IsCurrentHistorySearchNavigation(ticket))
                ShowError("No se pudo abrir el resultado", ex.Message);
        }
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

    private async Task<bool> LoadHistoryReviewAsync(
        SessionSummary session,
        HistoryLoadTicket ticket,
        string? selectedSegmentId = null,
        RevisionSelectionTicket? revisionTicket = null,
        bool refreshRevisionSelector = true,
        bool preserveRequestedSource = false,
        CorrectionEditorOperationTicket? editorTicket = null)
    {
        if (_store is null) return false;
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
        AnonymousVisualEvidenceProjection? visualProjection = await ProjectHistoryVisualEvidenceAsync(
            session.Id,
            reviewed.Select(item => item.Segment).ToArray(),
            ticket.CancellationToken);
        if (!_historyLoads.IsCurrent(ticket, SelectedHistorySession()?.Id)) return false;
        if (revisionTicket is not null && !IsCurrentRevisionSelection(revisionTicket)) return false;
        var refreshVisualAfterPublish = visualProjection is not null &&
            (!visualProjection.IsCurrent ||
             _anonymousVisualEvidenceProjector?.IsCurrent(visualProjection) != true);
        if (refreshVisualAfterPublish) visualProjection = null;
        IReadOnlyList<double> waveform = _audioArchive is null
            ? []
            : await AudioWaveformBuilder.BuildAsync(
                _audioArchive,
                session.StartedAt,
                selectedSourceChunks,
                cancellationToken: ticket.CancellationToken);
        if (!_historyLoads.IsCurrent(ticket, SelectedHistorySession()?.Id)) return false;
        if (revisionTicket is not null && !IsCurrentRevisionSelection(revisionTicket)) return false;
        if (!HistoryPlaybackAvailability.CanBeginOperation(_historyDeleteInProgress, _closing)) return false;
        if (!CanPublishCorrectionEditor(editorTicket)) return false;
        if (visualProjection is not null &&
            _anonymousVisualEvidenceProjector?.IsCurrent(visualProjection) != true)
        {
            refreshVisualAfterPublish = true;
            visualProjection = null;
        }
        var trackPublished = await ApplyHistoryTrackAsync(
            session,
            selectedSource,
            selectedSourceChunks,
            waveform,
            () =>
                _historyLoads.IsCurrent(ticket, SelectedHistorySession()?.Id) &&
                (revisionTicket is null || IsCurrentRevisionSelection(revisionTicket)) &&
                IsCorrectionEditorPublicationCurrent(editorTicket) &&
                HistoryPlaybackAvailability.CanBeginOperation(_historyDeleteInProgress, _closing));
        if (!trackPublished) return false;
        if (!_historyLoads.IsCurrent(ticket, SelectedHistorySession()?.Id)) return false;
        if (revisionTicket is not null && !IsCurrentRevisionSelection(revisionTicket)) return false;
        if (!HistoryPlaybackAvailability.CanBeginOperation(_historyDeleteInProgress, _closing)) return false;
        if (!CanPublishCorrectionEditor(editorTicket)) return false;
        _historyAudio = audio;
        _historySelectedSourceAudioComplete = IsCompleteRetainedSource(selectedSourceChunks, session.StartedAt);
        if (refreshRevisionSelector) PopulateHistoryRevisionSelector(revisions);
        HistoryAudioSummary.Text = audio.Count == 0 ? "No hay audio conservado" : string.Join(" · ", audio.Select(a =>
            $"{SourceName(a.Source)}: {a.ChunkCount} fragmentos, {a.Duration:hh\\:mm\\:ss}, {a.EncryptedBytes / 1_000_000.0:F1} MB"));
        _suppressHistorySegmentPlayback = true;
        try
        {
            _historyRows.Clear();
            foreach (var item in reviewed)
                _historyRows.Add(new(
                    item,
                    visualEvidence: visualProjection?.For(item.Segment),
                    reviewEligible: SegmentReviewActionsPresenter.IsSessionEligible(session.State)));
            HistorySegments.SelectedItem = _historyRows.FirstOrDefault(item => item.Segment.Id == selectedSegmentId);
        }
        finally { _suppressHistorySegmentPlayback = false; }
        if (_historyPlaying) UpdatePlaybackHighlight(CurrentPlaybackPosition());
        HistoryEmptyText.Visibility = reviewed.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyHistoryState(HistoryPresenter.Create(true, session.State, reviewed.Select(item => item.Segment).ToArray(), audio,
            selectedSource, _ => TranscriptPresentation.FormatReviewed(reviewed), preserveRequestedSource));
        UpdateSelectedSegmentEditor();
        if (refreshVisualAfterPublish)
            QueueHistoryVisualEvidenceRefresh(session.Id);
        return true;
    }

    private async Task<AnonymousVisualEvidenceProjection?> ProjectHistoryVisualEvidenceAsync(
        string sessionId,
        IReadOnlyList<TranscriptSegment> segments,
        CancellationToken cancellationToken)
    {
        var projector = _anonymousVisualEvidenceProjector;
        if (projector is null) return null;
        return await projector.ProjectAsync(
            AnonymousVisualEvidenceCacheScope.SelectedHistorySession,
            sessionId,
            segments,
            validatedRunIncomplete:
                string.Equals(_coordinator?.ActiveSessionId, sessionId, StringComparison.Ordinal) &&
                _anonymousVisualAnalysisProfileValidationState == VisualProbeProfileValidationState.Validated &&
                _anonymousVisualAnalysisStatus == AnonymousVisualAnalysisStatus.Running,
            cancellationToken: cancellationToken);
    }

    private async void HistoryAudioSource_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settingHistorySource || !_initialized) return;
        if (RestoreCorrectionDraftSourceSelectionIfNeeded()) return;
        var editorTicket = _correctionDraftNavigation.CaptureOperation();
        CancelHistoryRetranscription();
        _historyRevisionLoads.Invalidate();
        await SupersedePlaybackAsync();
        if (!CanPublishCorrectionEditor(editorTicket)) return;
        if (!HistoryPlaybackAvailability.CanBeginOperation(_historyDeleteInProgress, _closing)) return;
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
        try { await LoadHistoryReviewAsync(session, ticket, selectedSegmentId, editorTicket: editorTicket); }
        catch (OperationCanceledException) when (!_historyLoads.IsCurrent(ticket, SelectedHistorySession()?.Id)) { }
        catch (Exception ex) { if (_historyLoads.IsCurrent(ticket, SelectedHistorySession()?.Id)) ShowError("No se pudo cambiar la fuente de audio", ex.Message); }
    }

    private void HistorySegments_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressHistorySegmentSelectionChanged) return;
        if (RestoreCorrectionDraftSegmentSelectionIfNeeded()) return;
        UpdateSelectedSegmentEditor();
        var selected = SelectedHistorySegment();
        if (_suppressHistorySegmentPlayback || selected is null || _historyPlaying) return;
        if (_historyTrackSessionId == selected.Segment.SessionId && _historyTrackSource == selected.Segment.Source)
            SetPlaybackPosition(selected.Segment.Start);
    }

    private void PreviousHistorySegment_Click(object sender, RoutedEventArgs e) =>
        NavigateHistorySegment(previous: true);

    private void NextHistorySegment_Click(object sender, RoutedEventArgs e) =>
        NavigateHistorySegment(previous: false);

    private void NavigateHistorySegment(bool previous)
    {
        if (BlockCorrectionDraftNavigation()) return;
        var navigation = CurrentHistoryNavigation();
        var targetId = previous ? navigation.PreviousId : navigation.NextId;
        if (targetId is null) return;
        var target = _historyRows.FirstOrDefault(item =>
            string.Equals(item.Segment.Id, targetId, StringComparison.Ordinal));
        if (target is null) return;
        HistorySegments.SelectedItem = target;
        HistorySegments.ScrollIntoView(target);
    }

    private HistoryPlaybackNavigationState CurrentHistoryNavigation() =>
        HistoryPlaybackNavigation.Create(
            _historyRows.Select(item => item.Segment.Id).ToArray(),
            SelectedHistorySegment()?.Segment.Id);

    private async void HistorySegmentPlay_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not HistorySegmentItem item) return;
        var draft = _correctionDraftNavigation.Current;
        if (draft is not null &&
            (!string.Equals(draft.SessionId, item.Segment.SessionId, StringComparison.Ordinal) ||
             !string.Equals(draft.SegmentId, item.Segment.Id, StringComparison.Ordinal)))
        {
            AnnounceBlockedCorrectionDraftNavigation();
            e.Handled = true;
            return;
        }
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
            _correctionDraftNavigation.Clear();
            _settingCorrectionEditor = true;
            try
            {
                OriginalSegmentText.Clear();
                CorrectedSegmentText.Clear();
            }
            finally { _settingCorrectionEditor = false; }
            SelectedSegmentAudioText.Text = "Selecciona un fragmento de la transcripción para ubicar su audio exacto.";
            UpdateSegmentReviewStatus();
            UpdateHistoryControls();
            return;
        }
        if (modelRevision)
            _correctionDraftNavigation.Clear();
        else
            _correctionDraftNavigation.SetContext(
                selected.Segment.SessionId,
                selected.Segment.Id,
                selected.Segment.Source,
                selected.Text);
        _settingCorrectionEditor = true;
        try
        {
            OriginalSegmentText.Text = selected.Segment.Text;
            CorrectedSegmentText.Text = selected.Text;
        }
        finally { _settingCorrectionEditor = false; }
        SelectedSegmentAudioText.Text =
            $"Fragmento seleccionado: {FormatPlaybackTime(selected.Segment.Start)} · {TrackSourceName(selected.Segment.Source)}. Usa “Escuchar fragmento” para reproducir solamente esta parte.";
        if (!modelRevision && selected.Review.LatestRevision is { Action: CorrectionAction.SetText })
        {
            foreach (var candidate in GlossaryCandidateExtractor.Extract(selected.Segment.Text, selected.Text))
                _glossarySuggestions.Add(new(candidate.MistakenForm, candidate.PreferredTerm));

            GlossarySuggestionsPanel.Visibility = _glossarySuggestions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            GlossaryNoSuggestionsText.Visibility = _glossarySuggestions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        UpdateSegmentReviewStatus();
        UpdateHistoryControls();
    }

    private void CorrectedSegmentText_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_settingCorrectionEditor) return;
        var selected = SelectedHistorySegment();
        var session = SelectedHistorySession();
        if (selected is null || session is null ||
            HistoryRevisionSelector.SelectedItem is HistoryRevisionItem)
            _correctionDraftNavigation.Clear();
        else
            _correctionDraftNavigation.ObserveEditor(CorrectedSegmentText.Text);
        UpdateSegmentReviewStatus();
        UpdateHistoryControls();
    }

    private bool HasUnsavedCorrectionDraft() => _correctionDraftNavigation.HasUnsavedDraft;

    private bool BlockCorrectionDraftNavigation()
    {
        if (_correctionDraftNavigation.CanNavigate()) return false;
        AnnounceBlockedCorrectionDraftNavigation();
        return true;
    }

    private bool RestoreCorrectionDraftSegmentSelectionIfNeeded()
    {
        var draft = _correctionDraftNavigation.Current;
        if (draft is null) return false;
        var requested = SelectedHistorySegment();
        if (requested is not null &&
            string.Equals(requested.Segment.SessionId, draft.SessionId, StringComparison.Ordinal) &&
            string.Equals(requested.Segment.Id, draft.SegmentId, StringComparison.Ordinal))
            return false;

        var transition = requested is null
            ? new CorrectionDraftTransition(
                false,
                draft.SessionId,
                draft.SegmentId,
                draft.Source,
                draft.EditorText)
            : _correctionDraftNavigation.ResolveSegmentTransition(
                requested.Segment.SessionId,
                requested.Segment.Id,
                requested.Segment.Source,
                requested.Text);
        var preserved = _historyRows.FirstOrDefault(item =>
            string.Equals(item.Segment.SessionId, transition.SessionId, StringComparison.Ordinal) &&
            string.Equals(item.Segment.Id, transition.SegmentId, StringComparison.Ordinal));
        if (preserved is not null)
        {
            _suppressHistorySegmentSelectionChanged = true;
            try
            {
                HistorySegments.SelectedItem = preserved;
                HistorySegments.ScrollIntoView(preserved);
            }
            finally { _suppressHistorySegmentSelectionChanged = false; }
        }
        AnnounceBlockedCorrectionDraftNavigation();
        return true;
    }

    private bool RestoreCorrectionDraftSessionSelectionIfNeeded()
    {
        var draft = _correctionDraftNavigation.Current;
        if (draft is null ||
            string.Equals(SelectedHistorySession()?.Id, draft.SessionId, StringComparison.Ordinal))
            return false;
        var preserved = HistoryList.Items
            .OfType<HistorySessionItem>()
            .FirstOrDefault(item => string.Equals(item.Session.Id, draft.SessionId, StringComparison.Ordinal));
        if (preserved is not null)
        {
            _suppressHistoryListSelectionChanged = true;
            try
            {
                HistoryList.SelectedItem = preserved;
                HistoryList.ScrollIntoView(preserved);
            }
            finally { _suppressHistoryListSelectionChanged = false; }
        }
        AnnounceBlockedCorrectionDraftNavigation();
        return true;
    }

    private bool RestoreCorrectionDraftSourceSelectionIfNeeded()
    {
        var draft = _correctionDraftNavigation.Current;
        if (draft is null || SelectedHistorySource() == draft.Source) return false;
        _settingHistorySource = true;
        try
        {
            HistoryAudioSource.SelectedItem = HistoryAudioSource.Items
                .Cast<ComboBoxItem>()
                .First(item => string.Equals(
                    item.Tag?.ToString(),
                    draft.Source.ToString(),
                    StringComparison.Ordinal));
        }
        finally { _settingHistorySource = false; }
        AnnounceBlockedCorrectionDraftNavigation();
        return true;
    }

    private bool IsCorrectionEditorPublicationCurrent(CorrectionEditorOperationTicket? ticket) =>
        ticket is null || _correctionDraftNavigation.CanReplaceEditor(ticket.Value);

    private bool CanPublishCorrectionEditor(CorrectionEditorOperationTicket? ticket)
    {
        if (IsCorrectionEditorPublicationCurrent(ticket)) return true;
        RestoreCorrectionDraftUiAfterAwait();
        return false;
    }

    private void RestoreCorrectionDraftUiAfterAwait()
    {
        var draft = _correctionDraftNavigation.Current;
        if (draft is null) return;

        var session = HistoryList.Items
            .OfType<HistorySessionItem>()
            .FirstOrDefault(item => string.Equals(item.Session.Id, draft.SessionId, StringComparison.Ordinal));
        if (session is not null && !ReferenceEquals(HistoryList.SelectedItem, session))
        {
            _suppressHistoryListSelectionChanged = true;
            try { HistoryList.SelectedItem = session; }
            finally { _suppressHistoryListSelectionChanged = false; }
        }
        if (session is not null)
        {
            _historyLoads.Begin(draft.SessionId);
            UpdateSessionTitleEditor(session.Session);
        }
        _historyRevisionLoads.Invalidate();

        _settingHistorySource = true;
        try
        {
            HistoryAudioSource.SelectedItem = HistoryAudioSource.Items
                .Cast<ComboBoxItem>()
                .First(item => string.Equals(
                    item.Tag?.ToString(),
                    draft.Source.ToString(),
                    StringComparison.Ordinal));
        }
        finally { _settingHistorySource = false; }

        _settingHistoryRevision = true;
        try { HistoryRevisionSelector.SelectedIndex = 0; }
        finally { _settingHistoryRevision = false; }

        var segment = _historyRows.FirstOrDefault(item =>
            string.Equals(item.Segment.SessionId, draft.SessionId, StringComparison.Ordinal) &&
            string.Equals(item.Segment.Id, draft.SegmentId, StringComparison.Ordinal));
        if (segment is not null)
        {
            _suppressHistorySegmentSelectionChanged = true;
            try { HistorySegments.SelectedItem = segment; }
            finally { _suppressHistorySegmentSelectionChanged = false; }
        }

        _settingCorrectionEditor = true;
        try { CorrectedSegmentText.Text = draft.EditorText; }
        finally { _settingCorrectionEditor = false; }
        AnnounceBlockedCorrectionDraftNavigation();
    }

    private void AnnounceBlockedCorrectionDraftNavigation()
    {
        var status = CorrectionDraftNavigationGuard.BlockedStatus;
        SegmentReviewStatusText.Text = status;
        PendingReviewStatusText.Text = status;
        StatusText.Text = status;
        CorrectedSegmentText.Focus();
    }

    private void UpdateSegmentReviewStatus()
    {
        var selected = SelectedHistorySegment();
        if (selected is null)
        {
            SegmentReviewStatusText.Text = "Selecciona un segmento para revisar su estado.";
            return;
        }
        if (HistoryRevisionSelector.SelectedItem is HistoryRevisionItem)
        {
            SegmentReviewStatusText.Text = "Las versiones alternativas del modelo son de solo lectura.";
            return;
        }
        if (HasUnsavedCorrectionDraft())
        {
            SegmentReviewStatusText.Text = CorrectionDraftNavigationGuard.BlockedStatus;
            return;
        }
        if (!SegmentReviewActionsPresenter.IsSessionEligible(SelectedHistorySession()?.State) &&
            selected.Review.LatestRevision is null &&
            !selected.Review.IsOriginalApproved)
        {
            SegmentReviewStatusText.Text = "La revisión individual está disponible cuando la sesión finaliza.";
            return;
        }
        SegmentReviewStatusText.Text = selected.Review.LatestRevision switch
        {
            { Action: CorrectionAction.SetText } => "Corrección humana guardada; este segmento ya no está pendiente.",
            { Action: CorrectionAction.Undo } => "Revisión humana guardada; se restauró el texto original.",
            null when selected.Review.IsOriginalApproved => "Texto original marcado como revisado.",
            _ => "Pendiente de revisión. Corrige el texto o confirma que el original es correcto."
        };
    }

    private void DiscardCorrectionDraft_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedHistorySegment();
        if (selected is null) return;
        var savedText = _correctionDraftNavigation.DiscardDraft() ?? selected.Text;
        _settingCorrectionEditor = true;
        try { CorrectedSegmentText.Text = savedText; }
        finally { _settingCorrectionEditor = false; }
        UpdateSegmentReviewStatus();
        UpdateHistoryControls();
        StatusText.Text = "Borrador descartado; no se modificó la transcripción guardada.";
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

        var operation = await BeginPlaybackOperationAsync();
        if (operation is null) return;
        try
        {
            var source = selected.Segment.Source;
            var chunks = await _store.GetArchivedAudioAsync(session.Id, source, operation.CancellationToken);
            if (!IsCurrentPlayback(operation) || SelectedHistorySession()?.Id != session.Id || SelectedHistorySegment()?.Segment.Id != selected.Segment.Id) return;
            if (chunks.Count == 0)
            {
                StatusText.Text = $"No hay audio conservado de {TrackSourceName(source).ToLowerInvariant()} para este fragmento.";
                PlaybackStatusText.Text = "Audio no disponible para el fragmento seleccionado.";
                return;
            }

            var duration = selected.Segment.End - selected.Segment.Start;
            if (duration <= TimeSpan.Zero)
            {
                StatusText.Text = "Este fragmento no tiene un intervalo de audio válido.";
                PlaybackStatusText.Text = "El fragmento seleccionado no tiene un intervalo de audio válido.";
                return;
            }
            await PlayFromPositionAsync(
                CreatePlaybackTrack(session, source, chunks),
                selected.Segment.Start,
                duration,
                $"Fragmento · {TrackSourceName(source)}",
                allowSeeking: false,
                operation: operation);
        }
        catch (OperationCanceledException) when (!IsCurrentPlayback(operation)) { }
        catch (OperationCanceledException) { if (IsCurrentPlayback(operation)) StatusText.Text = "Reproducción detenida"; }
        catch (Exception ex)
        {
            if (IsCurrentPlayback(operation))
            {
                PlaybackStatusText.Text = "No se pudo preparar la reproducción.";
                ShowError("No se pudo reproducir el audio", ex.Message);
            }
        }
        finally { CompletePlaybackOperation(operation); }
    }
    private async void SaveCorrection_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedHistorySegment();
        var session = SelectedHistorySession();
        if (selected is null || session is null || _store is null || selected.Segment.SessionId != session.Id) return;
        CorrectionEditorSaveTicket saveTicket;
        try { saveTicket = _correctionDraftNavigation.CaptureSave(); }
        catch (InvalidOperationException) { return; }
        HistoryLoadTicket ticket;
        try { ticket = _historyLoads.Capture(session.Id); }
        catch (OperationCanceledException) { return; }
        try
        {
            var saveResult = await _correctionDraftNavigation.RunSaveAsync(
                saveTicket,
                (frozenText, cancellationToken) => _store.SaveCorrectionAsync(
                    selected.Segment.Id,
                    frozenText,
                    _settings.LocalDisplayName,
                    cancellationToken),
                ticket.CancellationToken);
            if (!_historyLoads.IsCurrent(ticket, SelectedHistorySession()?.Id)) return;
            var advance = saveResult.Baseline;
            if (!advance.CanReplaceEditor)
            {
                await LoadPendingReviewsAsync();
                StatusText.Text = advance.HasUnsavedDraft
                    ? "Corrección guardada; los cambios posteriores siguen sin guardar."
                    : "Corrección guardada; la vista cambió y no se reemplazó el editor.";
                UpdateSegmentReviewStatus();
                UpdateHistoryControls();
                return;
            }
            var reloadTicket = _correctionDraftNavigation.CaptureOperation();
            var published = await LoadHistoryReviewAsync(
                session,
                ticket,
                selected.Segment.Id,
                editorTicket: reloadTicket);
            await LoadPendingReviewsAsync();
            if (!published)
            {
                if (HasUnsavedCorrectionDraft())
                    StatusText.Text = "Corrección guardada; los cambios posteriores siguen sin guardar.";
                return;
            }
            StatusText.Text = "Corrección guardada; se conservó el texto original del modelo";
        }
        catch (OperationCanceledException) when (!_historyLoads.IsCurrent(ticket, SelectedHistorySession()?.Id)) { }
        catch (Exception ex) { if (_historyLoads.IsCurrent(ticket, SelectedHistorySession()?.Id)) ShowError("No se pudo guardar la corrección", ex.Message); }
    }

    private async void UndoCorrection_Click(object sender, RoutedEventArgs e)
    {
        if (BlockCorrectionDraftNavigation()) return;
        var selected = SelectedHistorySegment();
        var session = SelectedHistorySession();
        if (selected is null || session is null || _store is null || selected.Segment.SessionId != session.Id) return;
        HistoryLoadTicket ticket;
        try { ticket = _historyLoads.Capture(session.Id); }
        catch (OperationCanceledException) { return; }
        var editorTicket = _correctionDraftNavigation.CaptureOperation();
        try
        {
            var editorOperation = await _correctionDraftNavigation.RunAsync(
                editorTicket,
                cancellationToken => _store.UndoCorrectionAsync(
                    selected.Segment.Id,
                    _settings.LocalDisplayName,
                    cancellationToken),
                ticket.CancellationToken);
            if (!_historyLoads.IsCurrent(ticket, SelectedHistorySession()?.Id)) return;
            var advance = _correctionDraftNavigation.AdvanceSavedText(
                editorTicket,
                selected.Segment.Text);
            if (!editorOperation.CanReplaceEditor || !advance.CanReplaceEditor)
            {
                await LoadPendingReviewsAsync();
                StatusText.Text = advance.HasUnsavedDraft
                    ? "Corrección deshecha; los cambios escritos después siguen sin guardar."
                    : "Corrección deshecha; la vista cambió y no se reemplazó el editor.";
                UpdateSegmentReviewStatus();
                UpdateHistoryControls();
                return;
            }
            var reloadTicket = _correctionDraftNavigation.CaptureOperation();
            var published = await LoadHistoryReviewAsync(
                session,
                ticket,
                selected.Segment.Id,
                editorTicket: reloadTicket);
            await LoadPendingReviewsAsync();
            if (!published) return;
            StatusText.Text = "Corrección deshecha; se restauró el texto original del modelo";
        }
        catch (OperationCanceledException) when (!_historyLoads.IsCurrent(ticket, SelectedHistorySession()?.Id)) { }
        catch (Exception ex) { if (_historyLoads.IsCurrent(ticket, SelectedHistorySession()?.Id)) ShowError("No se pudo deshacer la corrección", ex.Message); }
    }

    private async void MarkSegmentReviewed_Click(object sender, RoutedEventArgs e) =>
        await ApplySegmentReviewDecisionAsync(SegmentReviewDecisionAction.ApproveOriginal);

    private async void ReopenSegmentReview_Click(object sender, RoutedEventArgs e) =>
        await ApplySegmentReviewDecisionAsync(SegmentReviewDecisionAction.Reopen);

    private async Task ApplySegmentReviewDecisionAsync(SegmentReviewDecisionAction action)
    {
        var selected = SelectedHistorySegment();
        var session = SelectedHistorySession();
        if (selected is null || session is null || _store is null ||
            selected.Segment.SessionId != session.Id || HasUnsavedCorrectionDraft())
            return;
        var actions = SegmentReviewActionsPresenter.Create(
            selected.Review,
            session.State,
            HistoryRevisionSelector.SelectedItem is HistoryRevisionItem,
            hasUnsavedDraft: false);
        if (action == SegmentReviewDecisionAction.ApproveOriginal && !actions.CanMarkReviewed ||
            action == SegmentReviewDecisionAction.Reopen && !actions.CanReopen)
            return;

        HistoryLoadTicket ticket;
        try { ticket = _historyLoads.Capture(session.Id); }
        catch (OperationCanceledException) { return; }
        var editorTicket = _correctionDraftNavigation.CaptureOperation();
        SegmentReviewStatusText.Text = action == SegmentReviewDecisionAction.ApproveOriginal
            ? "Guardando revisión…"
            : "Volviendo el segmento a pendiente…";
        UpdateHistoryControls();

        try
        {
            var expectedRevision = selected.Review.LatestDecision?.Revision ?? 0;
            var reviewer = string.IsNullOrWhiteSpace(_settings.LocalDisplayName)
                ? Environment.UserName
                : _settings.LocalDisplayName;
            if (string.IsNullOrWhiteSpace(reviewer)) reviewer = "Usuario local";
            var editorOperation = await _correctionDraftNavigation.RunAsync(
                editorTicket,
                cancellationToken => action == SegmentReviewDecisionAction.ApproveOriginal
                    ? _store.ApproveOriginalSegmentAsync(
                        session.Id,
                        selected.Segment.Id,
                        expectedRevision,
                        reviewer,
                        cancellationToken)
                    : _store.ReopenOriginalSegmentAsync(
                        session.Id,
                        selected.Segment.Id,
                        expectedRevision,
                        reviewer,
                        cancellationToken),
                ticket.CancellationToken);
            var result = editorOperation.Value;
            if (!_historyLoads.IsCurrent(ticket, SelectedHistorySession()?.Id)) return;

            var resultStatus = result.Status switch
            {
                SegmentReviewWriteStatus.Applied when action == SegmentReviewDecisionAction.ApproveOriginal =>
                    "Texto original marcado como revisado; no se creó una corrección.",
                SegmentReviewWriteStatus.Applied =>
                    "La aprobación original se volvió a dejar pendiente.",
                SegmentReviewWriteStatus.AlreadyCurrent =>
                    "El segmento ya estaba en ese estado; no se agregó otra revisión.",
                SegmentReviewWriteStatus.StateChanged =>
                    "El segmento cambió antes de guardar. Se actualizó la vista sin reintentar la acción.",
                _ => "Estado de revisión actualizado."
            };
            var editorIsCurrent = CanPublishCorrectionEditor(editorTicket);
            if (!editorOperation.CanReplaceEditor || !editorIsCurrent)
            {
                await LoadPendingReviewsAsync();
                StatusText.Text = $"{resultStatus} Los cambios escritos después siguen sin guardar.";
                return;
            }

            if (result.Status == SegmentReviewWriteStatus.Missing)
            {
                await RefreshHistoryAsync(ticket.CancellationToken, suppressSelectionChanged: true);
                if (!CanPublishCorrectionEditor(editorTicket)) return;
                await LoadPendingReviewsAsync();
                StatusText.Text = "El segmento ya no existe. El historial se actualizó sin reintentar la acción.";
                return;
            }

            var published = await LoadHistoryReviewAsync(
                session,
                ticket,
                selected.Segment.Id,
                editorTicket: editorTicket);
            if (!published) return;
            if (!_historyLoads.IsCurrent(ticket, SelectedHistorySession()?.Id)) return;
            await LoadPendingReviewsAsync();
            StatusText.Text = resultStatus;
        }
        catch (OperationCanceledException) when (!_historyLoads.IsCurrent(ticket, SelectedHistorySession()?.Id)) { }
        catch (Exception ex)
        {
            if (_historyLoads.IsCurrent(ticket, SelectedHistorySession()?.Id))
            {
                UpdateSegmentReviewStatus();
                ShowError("No se pudo guardar la revisión", ex.Message);
            }
        }
        finally { UpdateHistoryControls(); }
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
        var track = SelectedPlaybackTrack(session);
        if (track is null) return;
        var operation = await BeginPlaybackOperationAsync();
        if (operation is null) return;
        try
        {
            await PlayFromPositionAsync(
                track,
                playablePosition.Value,
                row.End - playablePosition.Value,
                $"Intervalo comparado · {TrackSourceName(_historyTrackSource)}",
                allowSeeking: false,
                operation: operation);
        }
        finally { CompletePlaybackOperation(operation); }
        e.Handled = true;
    }

    private async void HistoryRevisionSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settingHistoryRevision || !_initialized || _store is null) return;
        if (HasUnsavedCorrectionDraft())
        {
            _settingHistoryRevision = true;
            try { HistoryRevisionSelector.SelectedIndex = 0; }
            finally { _settingHistoryRevision = false; }
            AnnounceBlockedCorrectionDraftNavigation();
            return;
        }
        var editorTicket = _correctionDraftNavigation.CaptureOperation();
        await SupersedePlaybackAsync();
        if (!CanPublishCorrectionEditor(editorTicket)) return;
        if (!HistoryPlaybackAvailability.CanBeginOperation(_historyDeleteInProgress, _closing)) return;
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
                    refreshRevisionSelector: false,
                    editorTicket: editorTicket);
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
            var transcriptSegments = segments
                .Select(segment => new TranscriptSegment(
                    segment.Id,
                    session.Id,
                    selectedRevision.Revision.Source,
                    segment.Sequence,
                    segment.Start,
                    segment.End,
                    segment.Text,
                    selectedRevision.Revision.StartedAt))
                .ToArray();
            AnonymousVisualEvidenceProjection? visualProjection = await ProjectHistoryVisualEvidenceAsync(
                session.Id,
                transcriptSegments,
                revisionTicket.CancellationToken);
            if (!_historyLoads.IsCurrent(historyTicket, SelectedHistorySession()?.Id) ||
                !IsCurrentRevisionSelection(revisionTicket)) return;
            if (!CanPublishCorrectionEditor(editorTicket)) return;
            var refreshVisualAfterPublish = visualProjection is not null &&
                (!visualProjection.IsCurrent ||
                 _anonymousVisualEvidenceProjector?.IsCurrent(visualProjection) != true);
            if (refreshVisualAfterPublish) visualProjection = null;
            if (!CanPublishCorrectionEditor(editorTicket)) return;
            _suppressHistorySegmentPlayback = true;
            try
            {
                _historyRows.Clear();
                foreach (var transcript in transcriptSegments)
                {
                    _historyRows.Add(new(
                        new ReviewedTranscriptSegment(transcript, null),
                        "Versión generada por el modelo · sin correcciones humanas",
                        visualProjection?.For(transcript)));
                }
                HistorySegments.SelectedItem = null;
            }
            finally { _suppressHistorySegmentPlayback = false; }
            HistoryEmptyText.Visibility = segments.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            StatusText.Text = $"Viendo la versión generada por el modelo el {selectedRevision.Revision.StartedAt:yyyy-MM-dd HH:mm}; no se aplican correcciones humanas.";
            UpdateSelectedSegmentEditor();
            if (refreshVisualAfterPublish)
                QueueHistoryVisualEvidenceRefresh(session.Id);
        }
        catch (OperationCanceledException) when (!IsCurrentRevisionSelection(revisionTicket)) { }
        catch (Exception ex)
        {
            if (IsCurrentRevisionSelection(revisionTicket)) ShowError("No se pudo cargar la versión", ex.Message);
        }
    }
    private async void Retranscribe_Click(object sender, RoutedEventArgs e)
    {
        if (BlockCorrectionDraftNavigation()) return;
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
        var editorTicket = _correctionDraftNavigation.CaptureOperation();
        UpdateHistoryControls();
        try
        {
            StatusText.Text = "Retranscribiendo el audio cifrado conservado…";
            var editorOperation = await _correctionDraftNavigation.RunAsync(
                editorTicket,
                cancellationToken => _retranscription.RunAsync(
                    session,
                    SelectedHistorySource(),
                    modelPath,
                    _settings.Language,
                    cancellationToken),
                operation.CancellationToken);
            var revision = editorOperation.Value;
            if (!_historyRetranscriptionOperation.IsCurrent(operation) || !_historyLoads.IsCurrent(ticket, SelectedHistorySession()?.Id)) return;
            var editorIsCurrent = CanPublishCorrectionEditor(editorTicket);
            if (!editorOperation.CanReplaceEditor || !editorIsCurrent)
            {
                StatusText.Text = "La retranscripción terminó, pero los cambios escritos después siguen sin guardar; no se reemplazó el editor.";
                return;
            }
            var published = await LoadHistoryReviewAsync(session, ticket, editorTicket: editorTicket);
            if (!published) return;
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

    private async void PlaybackSpeedSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if ((sender as ComboBox)?.SelectedItem is not ComboBoxItem item ||
            !int.TryParse(item.Tag?.ToString(), out var percent) ||
            !PlaybackSpeed.TryCreate(percent, out var selectedSpeed)) return;

        if (selectedSpeed == _playbackSpeed) return;
        _playbackSpeed = selectedSpeed;
        if (!_initialized) return;

        if (!_historyPlaying || _activePlaybackOperation is null || _activePlaybackRequest is null)
        {
            PlaybackStatusText.Text =
                $"Velocidad {selectedSpeed.Label} seleccionada. {PlaybackToneNotice(selectedSpeed)}";
            UpdateHistoryControls();
            return;
        }

        if (!_playbackSpeedChanges.TryBeginChange(out var speedChange))
        {
            PlaybackStatusText.Text =
                $"Velocidad {selectedSpeed.Label} seleccionada. Se aplicará a la próxima reproducción.";
            UpdateHistoryControls();
            return;
        }
        var position = CurrentPlaybackPosition();
        var wasPaused = _historyPlaybackPaused;
        var request = _activePlaybackRequest;
        if (position >= request.EndPosition)
        {
            var stopGeneration = await SupersedePlaybackAsync(announce: false);
            if (!_playbackTransitionEpoch.IsCurrent(stopGeneration)) return;
            PlaybackStatusText.Text =
                $"Velocidad {selectedSpeed.Label} seleccionada. La reproducción ya había finalizado.";
            return;
        }

        var operation = await BeginPlaybackOperationAsync(speedChange);
        if (operation is null) return;
        try
        {
            if (!_playbackSpeedChanges.IsCurrent(speedChange) ||
                !IsCurrentPlayback(operation) ||
                _playbackSpeed != selectedSpeed) return;
            await PlayFromPositionAsync(
                request.Track,
                position,
                request.EndPosition - position,
                request.ContextLabel,
                request.AllowSeeking,
                operation,
                startPaused: wasPaused,
                requestedEndPosition: request.EndPosition,
                playbackSpeed: selectedSpeed);
        }
        finally { CompletePlaybackOperation(operation); }
    }

    private async void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_historyPlaying)
        {
            var control = _historyPlaybackPaused ? _playback.Resume() : _playback.Pause();
            if (!control.Succeeded)
            {
                var stopGeneration = await SupersedePlaybackAsync(announce: false);
                if (!_playbackTransitionEpoch.IsCurrent(stopGeneration)) return;
                PlaybackStatusText.Text = "No se pudo controlar la reproducción; el audio se detuvo.";
                ShowError("No se pudo controlar la reproducción", control.ErrorMessage!);
                return;
            }
            if (_historyPlaybackPaused)
            {
                _historyPlaybackPaused = false;
                StatusText.Text = "Reproducción reanudada";
                PlaybackStatusText.Text = "Reproducción reanudada.";
            }
            else
            {
                _historyPlaybackPaused = true;
                StatusText.Text = "Reproducción pausada";
                PlaybackStatusText.Text = "Reproducción pausada.";
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
        var track = SelectedPlaybackTrack(session);
        if (track is null) return;
        var operation = await BeginPlaybackOperationAsync();
        if (operation is null) return;
        try { await PlayFromPositionAsync(track, requested, null, TrackSourceName(_historyTrackSource), allowSeeking: true, operation: operation); }
        finally { CompletePlaybackOperation(operation); }
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
        if (_historyPlaying && !_activePlaybackAllowsSeeking)
        {
            StatusText.Text = "La reproducción de este fragmento está acotada; deténla para moverte por la pista seleccionada.";
            return;
        }
        requested = requested < TimeSpan.Zero ? TimeSpan.Zero : requested > _historyTrackDuration ? _historyTrackDuration : requested;
        if (!_historyPlaying)
        {
            var session = SelectedHistorySession();
            var playablePosition = session is null ? null : ResolvePlayablePosition(session, requested);
            if (playablePosition is null)
            {
                SetPlaybackPosition(requested);
                StatusText.Text = "No hay audio conservado en esa posición.";
                return;
            }
            SetPlaybackPosition(playablePosition.Value);
            StatusText.Text = playablePosition.Value == requested
                ? $"Posición ajustada a {FormatPlaybackTime(playablePosition.Value)}"
                : $"No había audio en esa posición; se avanzó a {FormatPlaybackTime(playablePosition.Value)}";
            return;
        }

        var activeSession = SelectedHistorySession();
        if (activeSession is null) return;
        var track = SelectedPlaybackTrack(activeSession);
        if (track is null) return;
        var operation = await BeginPlaybackOperationAsync();
        if (operation is null) return;
        try
        {
            SetPlaybackPosition(requested);
            await PlayFromPositionAsync(track, requested, null, TrackSourceName(_historyTrackSource), allowSeeking: true, operation: operation);
        }
        finally { CompletePlaybackOperation(operation); }
    }

    private async Task PlayFromPositionAsync(
        PlaybackTrackContext track,
        TimeSpan requestedPosition,
        TimeSpan? requestedDuration,
        string contextLabel,
        bool allowSeeking,
        PlaybackOperationRun operation,
        bool startPaused = false,
        TimeSpan? requestedEndPosition = null,
        PlaybackSpeed? playbackSpeed = null)
    {
        if (_audioArchive is null || track.Chunks.Count == 0 || !IsCurrentPlayback(operation)) return;
        try
        {
            var speed = playbackSpeed ?? _playbackSpeed;
            var maximumDuration = requestedEndPosition is { } preservedEnd
                ? preservedEnd - requestedPosition
                : requestedDuration
                    ?? (track.Duration > requestedPosition ? track.Duration - requestedPosition : TimeSpan.Zero);
            if (maximumDuration <= TimeSpan.Zero)
            {
                StatusText.Text = "No queda audio reproducible en esa posición.";
                PlaybackStatusText.Text = "No queda audio reproducible en esa posición.";
                return;
            }
            var plan = SegmentAudioNavigator.CreatePlan(
                track.Session.StartedAt,
                requestedPosition,
                track.Chunks,
                maximumDuration);
            if (plan is null)
            {
                StatusText.Text = "No hay audio conservado en esa posición.";
                PlaybackStatusText.Text = "No hay audio conservado en esa posición.";
                return;
            }

            _playbackBasePosition = plan.StartPosition;
            _activePlaybackTimeline = plan.Timeline;
            _activePlaybackSource = track.Source;
            _activePlaybackSessionId = track.Session.Id;
            _activePlaybackAllowsSeeking = allowSeeking;
            _activePlaybackRequest = new(
                track,
                requestedEndPosition ?? plan.Timeline.TimelineEnd,
                contextLabel,
                allowSeeking);
            PublishPlaybackPosition(plan.StartPosition);
            _historyPlaying = true;
            _historyPlaybackPaused = startPaused;
            _playbackTimer.Start();
            UpdatePlaybackHighlight(plan.StartPosition);
            StatusText.Text = startPaused
                ? $"Reproducción pausada en {FormatPlaybackTime(plan.StartPosition)} · velocidad {speed.Label}"
                : $"Reproduciendo {contextLabel.ToLowerInvariant()} desde {FormatPlaybackTime(plan.StartPosition)} · velocidad {speed.Label}";
            PlaybackStatusText.Text = startPaused
                ? $"Velocidad {speed.Label}. La reproducción permanece pausada. {PlaybackToneNotice(speed)}"
                : $"Velocidad {speed.Label}. Reproduciendo {contextLabel.ToLowerInvariant()}. {PlaybackToneNotice(speed)}";
            UpdateHistoryControls();

            await _playback.PlayPlanAsync(
                _audioArchive,
                plan,
                speed,
                startPaused,
                operation.CancellationToken);
            if (IsCurrentPlayback(operation))
            {
                StatusText.Text = $"Finalizó la reproducción de {contextLabel.ToLowerInvariant()}";
                PlaybackStatusText.Text = "Reproducción finalizada.";
            }
        }
        catch (OperationCanceledException) when (!IsCurrentPlayback(operation)) { }
        catch (OperationCanceledException)
        {
            if (IsCurrentPlayback(operation))
            {
                StatusText.Text = "Reproducción detenida";
                PlaybackStatusText.Text = "Reproducción detenida.";
            }
        }
        catch (Exception ex)
        {
            if (IsCurrentPlayback(operation))
            {
                PlaybackStatusText.Text = "La reproducción falló y se detuvo.";
                ShowError("No se pudo reproducir el audio", ex.Message);
            }
        }
        finally
        {
            if (IsCurrentPlayback(operation))
            {
                _playbackSpeedChanges.InvalidatePlaybackIntent();
                PublishPlaybackPosition(CurrentPlaybackPosition());
                _historyPlaying = false;
                _historyPlaybackPaused = false;
                _playbackTimer.Stop();
                _activePlaybackTimeline = null;
                _activePlaybackSource = null;
                _activePlaybackSessionId = null;
                _activePlaybackAllowsSeeking = false;
                _activePlaybackRequest = null;
                ClearPlaybackHighlight();
                UpdateHistoryControls();
            }
        }
    }

    private TimeSpan? ResolvePlayablePosition(SessionSummary session, TimeSpan requested)
    {
        if (_historyTrackSessionId != session.Id) return null;
        return _historyTrackTimeline?.ResolvePlayablePosition(requested);
    }

    private async Task<PlaybackOperationRun?> BeginPlaybackOperationAsync(
        PlaybackSpeedChangeTicket? speedChange = null)
    {
        IDisposable? playbackIntentTransition = null;
        if (speedChange is null)
        {
            playbackIntentTransition = _playbackSpeedChanges.BeginPlaybackIntentTransition();
            UpdateHistoryControls();
        }

        try
        {
            return await _playbackTransitions.RunAsync(async () =>
            {
                if (speedChange is { } queuedChange && !_playbackSpeedChanges.IsCurrent(queuedChange))
                    return null;
                if (!HistoryPlaybackAvailability.CanBeginOperation(_historyDeleteInProgress, _closing))
                {
                    StatusText.Text = "La reproducción no está disponible mientras se elimina la sesión o se cierra la aplicación.";
                    PlaybackStatusText.Text = "La reproducción no está disponible.";
                    return null;
                }
                await SupersedePlaybackCoreAsync(announce: false);
                if (speedChange is { } currentChange && !_playbackSpeedChanges.IsCurrent(currentChange))
                    return null;
                if (!HistoryPlaybackAvailability.CanBeginOperation(_historyDeleteInProgress, _closing))
                {
                    StatusText.Text = "La reproducción no está disponible mientras se elimina la sesión o se cierra la aplicación.";
                    PlaybackStatusText.Text = "La reproducción no está disponible.";
                    return null;
                }
                var operation = new PlaybackOperationRun(
                    _playbackOperations.Begin(),
                    CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token));
                _playbackTransitionEpoch.Advance();
                _activePlaybackOperation = operation;
                StatusText.Text = "Preparando reproducción de audio…";
                PlaybackStatusText.Text = "Preparando reproducción de audio.";
                UpdateHistoryControls();
                return operation;
            });
        }
        finally
        {
            playbackIntentTransition?.Dispose();
            if (playbackIntentTransition is not null) UpdateHistoryControls();
        }
    }

    private bool IsCurrentPlayback(PlaybackOperationRun operation) =>
        ReferenceEquals(_activePlaybackOperation, operation) &&
        !operation.CancellationToken.IsCancellationRequested &&
        _playbackOperations.IsCurrent(operation.Generation);

    private void CompletePlaybackOperation(PlaybackOperationRun operation)
    {
        if (ReferenceEquals(_activePlaybackOperation, operation))
            _activePlaybackOperation = null;
        operation.Complete();
        UpdateHistoryControls();
    }

    private void PlaybackTimer_Tick(object? sender, EventArgs e)
    {
        if (!_historyPlaying) return;
        var position = CurrentPlaybackPosition();
        PublishPlaybackPosition(position);
        UpdatePlaybackHighlight(position);
    }

    private async void StopPlayback_Click(object sender, RoutedEventArgs e)
    {
        _playbackSpeedChanges.InvalidatePlaybackIntent();
        if (_activePlaybackOperation is null && !_historyPlaying)
        {
            ClearPlaybackHighlight();
            PlaybackStatusText.Text = "Reproducción detenida.";
            StatusText.Text = "No hay audio en reproducción";
            UpdateHistoryControls();
            return;
        }
        var stopGeneration = await SupersedePlaybackAsync();
        if (!_playbackTransitionEpoch.IsCurrent(stopGeneration)) return;
        StatusText.Text = "Reproducción detenida";
    }

    private async Task<long> SupersedePlaybackAsync(bool announce = true)
    {
        var playbackIntentTransition = _playbackSpeedChanges.BeginPlaybackIntentTransition();
        UpdateHistoryControls();
        try
        {
            return await _playbackTransitions.RunAsync(() => SupersedePlaybackCoreAsync(announce));
        }
        finally
        {
            playbackIntentTransition.Dispose();
            UpdateHistoryControls();
        }
    }

    private async Task<long> SupersedePlaybackCoreAsync(bool announce)
    {
        if (_historyPlaying) PublishPlaybackPosition(CurrentPlaybackPosition());
        _playbackOperations.Supersede();
        var stopGeneration = _playbackTransitionEpoch.Advance();
        var operation = _activePlaybackOperation;
        operation?.Cancel();
        var operationCompletion = operation?.Completion ?? Task.CompletedTask;
        await Task.WhenAll(_playback.StopAsync(), operationCompletion);
        if (ReferenceEquals(_activePlaybackOperation, operation))
            _activePlaybackOperation = null;
        _playbackTimer.Stop();
        _historyPlaying = false;
        _historyPlaybackPaused = false;
        _activePlaybackTimeline = null;
        _activePlaybackSource = null;
        _activePlaybackSessionId = null;
        _activePlaybackAllowsSeeking = false;
        _activePlaybackRequest = null;
        ClearPlaybackHighlight();
        if (announce) PlaybackStatusText.Text = "Reproducción detenida.";
        UpdateHistoryControls();
        return stopGeneration;
    }

    private TimeSpan CurrentPlaybackPosition() =>
        _activePlaybackTimeline?.PositionAtElapsed(_playback.Elapsed)
        ?? _playbackBasePosition + _playback.Elapsed;

    private void PublishPlaybackPosition(TimeSpan position)
    {
        if (_activePlaybackSessionId == _historyTrackSessionId && _activePlaybackSource == _historyTrackSource)
            SetPlaybackPosition(position);
    }

    private void UpdatePlaybackHighlight(TimeSpan position)
    {
        var next = HistoryPlaybackHighlight.ResolveItem(
            _historyRows,
            static item => item.Segment,
            _activePlaybackSource ?? _historyTrackSource,
            position);
        if (ReferenceEquals(_highlightedHistoryRow, next)) return;
        _highlightedHistoryRow?.SetPlaybackActive(false);
        _highlightedHistoryRow = next;
        _highlightedHistoryRow?.SetPlaybackActive(true);
        if (_highlightedHistoryRow is not null) HistorySegments.ScrollIntoView(_highlightedHistoryRow);
        if (_historyPlaying)
        {
            PlaybackStatusText.Text = next is null
                ? "Reproducción en un intervalo sin transcripción."
                : $"Reproduciendo {next.Header}.";
        }
    }

    private void ClearPlaybackHighlight()
    {
        _highlightedHistoryRow?.SetPlaybackActive(false);
        _highlightedHistoryRow = null;
    }

    private PlaybackTrackContext? SelectedPlaybackTrack(SessionSummary session) =>
        _historyTrackSessionId == session.Id && _historyTrackChunks.Count > 0
            ? CreatePlaybackTrack(session, _historyTrackSource, _historyTrackChunks)
            : null;

    private static PlaybackTrackContext CreatePlaybackTrack(
        SessionSummary session,
        AudioSourceKind source,
        IReadOnlyList<ArchivedAudioChunk> chunks) =>
        new(
            session,
            source,
            chunks,
            AudioWaveformBuilder.GetTimelineDuration(session.StartedAt, chunks));

    private Task<bool> ApplyHistoryTrackAsync(
        SessionSummary session,
        AudioSourceKind source,
        IReadOnlyList<ArchivedAudioChunk> chunks,
        IReadOnlyList<double> waveform,
        Func<bool> canPublish)
    {
        ArgumentNullException.ThrowIfNull(canPublish);
        return _playbackTransitions.RunAsync(async () =>
        {
            if (!canPublish()) return false;
            var playbackIntentTransition = _playbackSpeedChanges.BeginPlaybackIntentTransition();
            UpdateHistoryControls();
            try
            {
                if (_historyPlaying || _activePlaybackOperation is not null)
                    await SupersedePlaybackCoreAsync(announce: true);
                if (!canPublish()) return false;

                var trackChanged = _historyTrackSessionId != session.Id || _historyTrackSource != source;
                _historyTrackSessionId = session.Id;
                _historyTrackSource = source;
                _historyTrackChunks = chunks;
                _historyTrackTimeline = PlaybackTimelineMap.Create(session.StartedAt, chunks);
                _historyTrackDuration = AudioWaveformBuilder.GetTimelineDuration(session.StartedAt, chunks);
                _waveformBars.Clear();
                foreach (var height in waveform) _waveformBars.Add(height);
                PlaybackTimeline.Maximum = Math.Max(1, _historyTrackDuration.TotalSeconds);
                PlaybackTrackText.Text = chunks.Count == 0
                    ? $"{TrackSourceName(source)} · sin audio conservado"
                    : $"{TrackSourceName(source)} · {FormatPlaybackTime(_historyTrackDuration)} conservados";
                if (trackChanged || chunks.Count == 0)
                {
                    ClearPlaybackHighlight();
                    SetPlaybackPosition(TimeSpan.Zero);
                }
                else SetPlaybackPosition(TimeSpan.FromSeconds(Math.Min(PlaybackTimeline.Value, _historyTrackDuration.TotalSeconds)));
                return true;
            }
            finally
            {
                playbackIntentTransition.Dispose();
                UpdateHistoryControls();
            }
        });
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
        if (BlockCorrectionDraftNavigation()) return;
        if (session.State is SessionState.Recording or SessionState.Paused || string.Equals(_coordinator?.ActiveSessionId, session.Id, StringComparison.Ordinal))
        {
            const string message = "Esta sesión sigue activa. Detenla antes de eliminarla.";
            StatusText.Text = message;
            HistoryActionGuidance.Text = message;
            return;
        }
        try
        {
            if (MessageBox.Show(this, $"¿Eliminar definitivamente '{session.Title}', su transcripción, todo el audio conservado y las entradas del diccionario originadas en sus correcciones? Esta acción no se puede deshacer.", "Eliminar sesión guardada", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            _historyDeleteInProgress = true;
            _historySearch.Invalidate();
            await CancelHistoryNavigationAsync();
            _historyLoads.Invalidate();
            _historyRevisionLoads.Invalidate();
            HideHistorySearchResults();
            HistorySearchStatusText.Text = HistorySearchPresenter.IdleStatus;
            UpdateHistoryControls();
            await _historySearchActivity.BlockAndDrainAsync();
            await CancelPendingReviewLoadAsync();
            await SupersedePlaybackAsync(announce: false);
            if (_audioArchive is not null) await _audioArchive.DeleteSessionAsync(session.Id);
            else await _store.DeleteSessionAsync(session.Id);
            _anonymousVisualEvidenceProjector?.Clear(
                AnonymousVisualEvidenceCacheScope.SelectedHistorySession,
                session.Id);
            ClearHistoryReview();
            HistoryAudioSummary.Text = HistoryPresenter.SelectSessionMessage;
            StatusText.Text = "Sesión eliminada";
            await RefreshHistoryAsync();
        }
        catch (Exception ex) { ShowError("No se pudo eliminar la sesión", ex.Message); }
        finally
        {
            _historyDeleteInProgress = false;
            if (!_closing) _historySearchActivity.Reopen();
            UpdateHistoryControls();
            if (!_closing) await LoadPendingReviewsAsync();
        }
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
        var navigation = CurrentHistoryNavigation();
        var hasTrack = _historyTrackChunks.Count > 0 && _historyTrackDuration > TimeSpan.Zero;
        var playbackActive = _activePlaybackOperation is not null || _historyPlaying;
        var playbackBlocked = !HistoryPlaybackAvailability.CanBeginOperation(_historyDeleteInProgress, _closing);
        var hasUnsavedDraft = HasUnsavedCorrectionDraft();
        var selectedTrackReady = _historyState.CanPlayAudio && hasTrack;
        var canPauseActive = _historyPlaying && _activePlaybackOperation is not null;
        var canSeek = !playbackBlocked &&
            (!playbackActive ? selectedTrackReady : _historyPlaying && _activePlaybackAllowsSeeking);
        HistoryWorkspaceTabs.IsEnabled = !playbackBlocked;
        RefreshHistoryButton.IsEnabled = _initialized && !playbackBlocked && !hasUnsavedDraft;
        HistoryList.IsEnabled = !playbackBlocked && !hasUnsavedDraft;
        HistorySearchResults.IsEnabled = !playbackBlocked && !hasUnsavedDraft;
        HistorySegments.IsEnabled = !playbackBlocked && !hasUnsavedDraft;
        HistoryAudioSource.IsEnabled = !playbackBlocked && !hasUnsavedDraft && _historyState.CanChooseSource && !playbackActive && !_historyRetranscriptionOperation.IsRunning;
        HistoryRevisionSelector.IsEnabled = !playbackBlocked && !hasUnsavedDraft && _historyState.HasSelection && !playbackActive && !_historyRetranscriptionOperation.IsRunning;
        PlayPauseButton.IsEnabled = !playbackBlocked && (canPauseActive || !playbackActive && selectedTrackReady);
        PlayPauseButton.Content = _historyPlaying
            ? (_historyPlaybackPaused ? "Continuar" : "Pausar")
            : "Reproducir";
        SeekBackButton.IsEnabled = canSeek;
        SeekForwardButton.IsEnabled = canSeek;
        StopPlaybackButton.IsEnabled = !playbackBlocked && playbackActive;
        PlaybackTimeline.IsEnabled = canSeek;
        PlaybackSpeedSelector.IsEnabled = !playbackBlocked &&
            !_playbackSpeedChanges.IsPlaybackIntentTransitionActive &&
            (_activePlaybackOperation is null || _historyPlaying);
        PreviousHistorySegmentButton.IsEnabled = !playbackBlocked && !hasUnsavedDraft && navigation.CanPrevious;
        NextHistorySegmentButton.IsEnabled = !playbackBlocked && !hasUnsavedDraft && navigation.CanNext;
        ExportWavButton.IsEnabled = !playbackBlocked && _historyState.CanExportWav && !playbackActive;
        ExportTxtButton.IsEnabled = !playbackBlocked && _historyState.CanExportTxt;
        ExportObsidianButton.IsEnabled = !playbackBlocked && _historyState.CanExportMarkdown;
        DeleteSessionButton.IsEnabled = !playbackBlocked && !hasUnsavedDraft && _historyState.CanDelete && !playbackActive && !_historyRetranscriptionOperation.IsRunning;
        var viewingModelRevision = HistoryRevisionSelector.SelectedItem is HistoryRevisionItem;
        CorrectionPanel.IsEnabled = !playbackBlocked && selected is not null && !viewingModelRevision;
        var reviewActions = SegmentReviewActionsPresenter.Create(
            selected?.Review,
            SelectedHistorySession()?.State,
            viewingModelRevision,
            hasUnsavedDraft);
        SaveCorrectionButton.IsEnabled = !playbackBlocked && selected is not null && !viewingModelRevision && hasUnsavedDraft;
        DiscardCorrectionDraftButton.IsEnabled = !playbackBlocked && hasUnsavedDraft;
        MarkSegmentReviewedButton.IsEnabled = !playbackBlocked && reviewActions.CanMarkReviewed;
        ReopenSegmentReviewButton.IsEnabled = !playbackBlocked && reviewActions.CanReopen;
        UndoCorrectionButton.IsEnabled = !playbackBlocked && !hasUnsavedDraft && selected?.Review.IsCorrected == true && !viewingModelRevision;
        AddGlossaryButton.IsEnabled = !playbackBlocked && _glossarySuggestions.Count > 0 && !viewingModelRevision;
        var retranscription = HistoryRetranscriptionPresenter.Create(
            SelectedHistorySession()?.State,
            _historyState.HasSelectedSourceAudio && _historySelectedSourceAudioComplete,
            _historyRetranscriptionOperation.IsRunning);
        RetranscribeButton.IsEnabled = !playbackBlocked && !hasUnsavedDraft && retranscription.CanStart && !playbackActive;
        CancelRetranscriptionButton.IsEnabled = !playbackBlocked && retranscription.CanCancel;
        RetranscribeButton.ToolTip = retranscription.Guidance;
        CancelRetranscriptionButton.ToolTip = retranscription.Guidance;

        var playbackSource = _activePlaybackSource ?? _historyTrackSource;
        PlayPauseButton.ToolTip = _historyState.CanPlayAudio || canPauseActive
            ? $"Reproduce o pausa la pista {TrackSourceName(playbackSource).ToLowerInvariant()}."
            : _historyState.AudioGuidance;
        SeekBackButton.ToolTip = "Retrocede 10 segundos en la pista seleccionada.";
        SeekForwardButton.ToolTip = "Avanza 10 segundos en la pista seleccionada.";
        PlaybackSpeedSelector.ToolTip =
            $"Velocidad actual: {_playbackSpeed.Label}. Cambiarla modifica el tono y no se guarda al cerrar la aplicación.";
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
        UpdateHistorySearchControls();
        UpdatePendingReviewControls();
    }

    private void UpdatePendingReviewControls()
    {
        if (RefreshPendingReviewsButton is null ||
            ApproveSelectedPendingReviewsButton is null ||
            PendingReviewList is null)
            return;
        var canInteract = _initialized && !_closing && !_historyDeleteInProgress &&
                          !_pendingReviewOperation.IsRunning && !HasUnsavedCorrectionDraft();
        var selection = PendingReviewBatchSelectionPresenter.Create(
            _pendingReviewRows,
            canInteract);
        RefreshPendingReviewsButton.IsEnabled = canInteract;
        PendingReviewList.IsEnabled = canInteract;
        ApproveSelectedPendingReviewsButton.Content = selection.ButtonLabel;
        ApproveSelectedPendingReviewsButton.IsEnabled = selection.CanApprove;
    }
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closingConfirmed) return;
        e.Cancel = true;
        if (_closing) return;
        if (_recording && MessageBox.Show(this, "Hay una transcripción activa. ¿Deseas detenerla y salir?", "Sesión activa", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (HasUnsavedCorrectionDraft())
        {
            var discard = MessageBox.Show(
                this,
                "Hay una corrección sin guardar. Selecciona Sí para descartar el borrador y cerrar, o No para volver y guardarlo.",
                "Cambios sin guardar",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (discard != MessageBoxResult.Yes)
            {
                AnnounceBlockedCorrectionDraftNavigation();
                return;
            }
            _correctionDraftNavigation.Clear();
        }
        _closing = true;
        _historySearch.Invalidate();
        await CancelHistoryNavigationAsync();
        _historyLoads.Invalidate();
        _historyRevisionLoads.Invalidate();
        await CancelPendingReviewLoadAsync();
        await _glossaryWorkspaceOperation.CancelAndWaitAsync();
        _glossaryWorkspaceEntries = [];
        _glossaryWorkspaceRows.Clear();
        await _historySearchActivity.BlockAndDrainAsync();
        await SupersedePlaybackAsync(announce: false);
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
                    _historySearch.Dispose();
                    _historySearchNavigation.Dispose();
                    _historyNavigationOperation.Dispose();
                    _historyNavigationTransition.Dispose();
                    _pendingReviewLoads.Dispose();
                    _pendingReviewOperation.Dispose();
                    _pendingReviewTransition.Dispose();
                    _historyRetranscriptionOperation.Dispose();
                    _glossaryWorkspaceOperation.Dispose();
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
    private async Task<(HistorySearchNavigationTicket Ticket, OwnedCancellationOperationCoordinator.Operation Operation)?> BeginHistoryNavigationAsync(
        HistorySearchNavigationIntent intent)
    {
        await _historyNavigationTransition.WaitAsync();
        try
        {
            _historySearchNavigation.Invalidate();
            await _historyNavigationOperation.CancelAndWaitAsync();
            if (!_historySearchActivity.IsAccepting ||
                !HistoryPlaybackAvailability.CanBeginOperation(_historyDeleteInProgress, _closing))
                return null;

            var ticket = _historySearchNavigation.Begin(CreateHistorySearchNavigationKey(intent));
            if (!_historyNavigationOperation.TryBegin(
                    [_lifetime.Token, ticket.CancellationToken],
                    out var operation))
            {
                _historySearchNavigation.Invalidate();
                return null;
            }
            return (ticket, operation);
        }
        finally { _historyNavigationTransition.Release(); }
    }
    private async Task CancelHistoryNavigationAsync()
    {
        await _historyNavigationTransition.WaitAsync();
        try
        {
            _historySearchNavigation.Invalidate();
            await _historyNavigationOperation.CancelAndWaitAsync();
        }
        finally { _historyNavigationTransition.Release(); }
    }
    private AudioSourceKind SelectedHistorySource() => (HistoryAudioSource.SelectedItem as ComboBoxItem)?.Tag?.ToString() == nameof(AudioSourceKind.SystemOutput) ? AudioSourceKind.SystemOutput : AudioSourceKind.Microphone;
    private SessionSummary? SelectedHistorySession() => (HistoryList.SelectedItem as HistorySessionItem)?.Session;
    private HistorySegmentItem? SelectedHistorySegment() => HistorySegments.SelectedItem as HistorySegmentItem;
    private string? SelectedHistoryRevisionId() => (HistoryRevisionSelector.SelectedItem as HistoryRevisionItem)?.Revision.Id;
    private static HistorySearchNavigationKey CreateHistorySearchNavigationKey(HistorySearchNavigationIntent intent) =>
        new(intent.SessionId, intent.SegmentId, intent.Source);
    private HistorySearchNavigationKey? SelectedHistorySearchNavigationKey() =>
        HistorySearchResults.SelectedItem is HistorySearchResultItem selected
            ? CreateHistorySearchNavigationKey(HistorySearchNavigationIntent.From(selected.Hit))
            : null;
    private HistorySearchNavigationKey? SelectedPendingReviewNavigationKey() =>
        PendingReviewList.SelectedItem is PendingReviewItem selected
            ? CreateHistorySearchNavigationKey(new(
                selected.Pending.SessionId,
                selected.Pending.SegmentId,
                selected.Pending.Source,
                UseOriginalRevision: true,
                AutoPlay: false))
            : null;
    private bool IsCurrentHistorySearchNavigation(HistorySearchNavigationTicket ticket) =>
        _historySearchNavigation.IsCurrent(ticket) &&
        (ticket.Key == SelectedHistorySearchNavigationKey() ||
         ticket.Key == SelectedPendingReviewNavigationKey());
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
        _historyTrackTimeline = null;
        _historyTrackDuration = TimeSpan.Zero;
        _historyTrackSessionId = null;
        _waveformBars.Clear();
        _historySelectedSourceAudioComplete = false;
        PlaybackTrackText.Text = "Selecciona una sesión y una pista de audio.";
        PlaybackStatusText.Text = "Reproducción detenida.";
        ClearPlaybackHighlight();
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

    private sealed record PlaybackTrackContext(
        SessionSummary Session,
        AudioSourceKind Source,
        IReadOnlyList<ArchivedAudioChunk> Chunks,
        TimeSpan Duration);

    private sealed record PlaybackRequestContext(
        PlaybackTrackContext Track,
        TimeSpan EndPosition,
        string ContextLabel,
        bool AllowSeeking);

    private static string PlaybackToneNotice(PlaybackSpeed speed) =>
        speed == PlaybackSpeed.Normal
            ? "A 1× se conserva el tono original."
            : "Esta velocidad cambia el tono.";

    private sealed class PlaybackOperationRun(long generation, CancellationTokenSource cancellation)
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _completed;
        public long Generation { get; } = generation;
        public CancellationToken CancellationToken => cancellation.Token;
        public Task Completion => _completion.Task;
        public void Cancel()
        {
            try { cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }
        public void Complete()
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0) return;
            cancellation.Dispose();
            _completion.TrySetResult();
        }
    }

}













