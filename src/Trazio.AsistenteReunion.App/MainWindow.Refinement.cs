using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation.Peers;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.App;

public partial class MainWindow
{
    private readonly ObservableCollection<RefinementBatchItem> _refinementBatches = [];
    private readonly ObservableCollection<RefinementProposalItem> _refinementProposals = [];
    private readonly ObservableCollection<JointQwenRevisionItem> _jointQwenRevisions = [];
    private string? _preferredQwenRevisionId;
    private readonly ObservableCollection<RefinementDiffToken> _refinementOriginalWords = [];
    private readonly ObservableCollection<RefinementDiffToken> _refinementProposalWords = [];
    private readonly OwnedCancellationOperationCoordinator _refinementOperation = new();
    private readonly ConservativeTranscriptRefinementGenerator _refinementGenerator = new();
    private readonly LocalLayaSidecarService _localLayaSidecar = new();
    private readonly RefinementJevEvaluator _jevEvaluator = new();
    private RefinementLayaFailure? _jevLocalFailure;
    private string? _jevFailureBatchId;
    private int _layaEvaluationLoadVersion;
    private int _refinementRefreshGeneration;
    private bool _settingRefinementSelection;
    private bool _refinementLoading;

    private void InitializeRefinementUi()
    {
        RefinementBatchSelector.ItemsSource = _refinementBatches;
        RefinementProposalsList.ItemsSource = _refinementProposals;
        JointQwenRevisionSelector.ItemsSource = _jointQwenRevisions;
        RefinementOriginalDiff.ItemsSource = _refinementOriginalWords;
        RefinementProposalDiff.ItemsSource = _refinementProposalWords;
    }

    private bool IsSelectedOriginal(string sessionId, string segmentId) =>
        !_closing && HistoryRevisionSelector.SelectedItem is not HistoryRevisionItem &&
        SelectedHistorySession()?.Id == sessionId &&
        SelectedHistorySegment()?.Segment.Id == segmentId;

    private void QueueRefinementPanelRefresh()
    {
        _refinementOperation.Cancel();
        _jevLocalFailure = null;
        _jevFailureBatchId = null;
        var generation = ++_refinementRefreshGeneration;
        _preferredQwenRevisionId = (JointQwenRevisionSelector.SelectedItem as JointQwenRevisionItem)?.Revision.Id;
        _jointQwenRevisions.Clear();
        _refinementBatches.Clear();
        _refinementProposals.Clear();
        _refinementOriginalWords.Clear();
        _refinementProposalWords.Clear();
        SetRefinementSensitiveWarning("Selecciona una propuesta para revisar posibles cambios sensibles.");
        LayaEvaluationHistoryText.Text = "Sin evaluaciones locales guardadas.";
        var session = SelectedHistorySession();
        var segment = SelectedHistorySegment();
        if (_store is null || session is null || segment is null || !IsSelectedOriginal(session.Id, segment.Segment.Id))
        {
            RefinementStatusText.Text = "Selecciona un segmento de la transcripción original.";
            UpdateRefinementControls();
            return;
        }
        _refinementLoading = true;
        RefinementStatusText.Text = "Cargando propuestas guardadas…";
        UpdateRefinementControls();
        _ = RefreshRefinementPanelAsync(session.Id, segment.Segment.Id, generation);
    }

    private async Task RefreshRefinementPanelAsync(string sessionId, string segmentId, int generation)
    {
        if (_store is null) return;
        try
        {
            var row = SelectedHistorySegment();
            if (row?.Segment.Id != segmentId) return;
            var batches = await _store.ListRefinementBatchesAsync(sessionId, _lifetime.Token);
            var revisions = await _store.ListModelRevisionsAsync(sessionId, row.Segment.Source,
                successfulOnly: true, _lifetime.Token);
            if (generation != _refinementRefreshGeneration || !IsSelectedOriginal(sessionId, segmentId)) return;
            _settingRefinementSelection = true;
            try
            {
                foreach (var batch in batches.Where(item => item.SegmentId == segmentId).OrderByDescending(item => item.CreatedAt))
                    _refinementBatches.Add(new(batch));
                foreach (var revision in revisions.Where(item =>
                    item.ProducerIdentity == ModelRevisionProducer.Qwen3AsrLlamaCppV1 &&
                    item.Scope?.SegmentId == segmentId &&
                    item.Scope.Start == row.Segment.Start && item.Scope.End == row.Segment.End &&
                    !string.IsNullOrWhiteSpace(item.ModelHash)))
                    _jointQwenRevisions.Add(new(revision));
                JointQwenRevisionSelector.SelectedItem = _jointQwenRevisions.FirstOrDefault(item =>
                    item.Revision.Id == _preferredQwenRevisionId);
                RefinementBatchSelector.SelectedIndex = _refinementBatches.Count > 0 ? 0 : -1;
                ShowSelectedRefinementBatch();
            }
            finally { _settingRefinementSelection = false; }
            RefinementStatusText.Text = _refinementBatches.Count == 0
                ? "Todavía no hay análisis guardados para este fragmento."
                : _refinementBatches[0].Batch.Proposals.Count == 0
                    ? "El análisis más reciente no encontró una mejora segura; se conservó el original."
                    : "Compara los cambios y escucha el audio antes de decidir.";
        }
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception)
        {
            if (generation == _refinementRefreshGeneration && IsSelectedOriginal(sessionId, segmentId))
                RefinementStatusText.Text = "No se pudieron cargar las propuestas guardadas.";
        }
        finally
        {
            if (generation == _refinementRefreshGeneration)
            {
                _refinementLoading = false;
                UpdateRefinementControls();
            }
        }
    }

    private void SetRefinementSensitiveWarning(string message)
    {
        if (RefinementSensitiveWarningText.Text == message) return;
        RefinementSensitiveWarningText.Text = message;
        if (AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
            UIElementAutomationPeer.CreatePeerForElement(RefinementSensitiveWarningText)?
                .RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void ShowSelectedRefinementBatch()
    {
        _refinementProposals.Clear();
        _refinementOriginalWords.Clear();
        _refinementProposalWords.Clear();
        if (RefinementBatchSelector.SelectedItem is not RefinementBatchItem item)
        {
            SetRefinementSensitiveWarning("Selecciona una propuesta para revisar posibles cambios sensibles.");
            LayaEvaluationHistoryText.Text = "Sin evaluaciones locales guardadas.";
            return;
        }
        foreach (var proposal in item.Batch.Proposals) _refinementProposals.Add(new(proposal));
        _ = RefreshSelectedLayaEvaluationsAsync(item.Batch.Id);
        RefinementProposalsList.SelectedIndex = _refinementProposals.Count > 0 ? 0 : -1;
        ShowSelectedRefinementDiff();
    }

    private void ShowSelectedRefinementDiff()
    {
        _refinementOriginalWords.Clear();
        _refinementProposalWords.Clear();
        if (RefinementBatchSelector.SelectedItem is not RefinementBatchItem batch ||
            RefinementProposalsList.SelectedItem is not RefinementProposalItem proposal)
        {
            SetRefinementSensitiveWarning("Selecciona una propuesta para revisar posibles cambios sensibles.");
            return;
        }
        var diff = RefinementWordDiff.Compare(batch.Batch.OriginalText, proposal.Proposal.Text);
        SetRefinementSensitiveWarning(diff.SensitiveChangeSummary);
        foreach (var token in diff.Original) _refinementOriginalWords.Add(token);
        foreach (var token in diff.Proposal) _refinementProposalWords.Add(token);
    }

    private void RefinementBatchSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settingRefinementSelection) return;
        _refinementOperation.Cancel();
        _jevLocalFailure = null;
        _jevFailureBatchId = null;
        ShowSelectedRefinementBatch();
        UpdateRefinementControls();
    }

    private void RefinementProposalsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settingRefinementSelection) return;
        ShowSelectedRefinementDiff();
        UpdateRefinementControls();
    }

    private void JointQwenRevisionSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settingRefinementSelection) return;
        _refinementOperation.Cancel();
        _jevLocalFailure = null;
        _jevFailureBatchId = null;
        UpdateRefinementControls();
    }

    private async void GenerateRefinement_Click(object sender, RoutedEventArgs e)
    {
        if (_store is null || _settings.ExternalAiProvider is not { } provider ||
            SelectedHistorySession() is not { } session || SelectedHistorySegment() is not { } row ||
            !IsSelectedOriginal(session.Id, row.Segment.Id) || HasUnsavedCorrectionDraft()) return;
        HistoryLoadTicket ticket;
        try { ticket = _historyLoads.Capture(session.Id); }
        catch (OperationCanceledException) { return; }
        if (!_refinementOperation.TryBegin([ticket.CancellationToken, _lifetime.Token], out var operation)) return;
        var editorTicket = _correctionDraftNavigation.CaptureOperation();
        RefinementStatusText.Text = "Analizando el fragmento…";
        UpdateRefinementControls();
        try
        {
            var reviewed = await _store.GetReviewedSegmentsAsync(session.Id, operation.CancellationToken);
            var glossary = await _store.ListGlossaryAsync(operation.CancellationToken);
            var neighbors = reviewed.Where(item => item.Segment.Id != row.Segment.Id &&
                    item.Segment.Source == row.Segment.Source &&
                    (item.IsCorrected || item.IsOriginalApproved))
                .Select(item => new TranscriptContextItem(item.Segment.Start, item.Segment.End,
                    item.EffectiveText, HumanApproved: true));
            var context = TranscriptContextPlanner.Build(neighbors, row.Segment.Start, row.Segment.End,
                glossary.Where(item => item.IsActive).Select(item => item.PreferredTerm));
            if (!IsRefinementOperationCurrent(operation, ticket, row, editorTicket)) return;
            var drafts = await _refinementGenerator.GenerateAsync(row.Segment, context, provider,
                (preview, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!IsRefinementOperationCurrent(operation, ticket, row, editorTicket)) return Task.FromResult(false);
                    try
                    {
                        var dialog = new RefinementOutboundConsentDialog(preview) { Owner = this };
                        var authorized = dialog.ShowDialog() == true;
                        return Task.FromResult(authorized && IsRefinementOperationCurrent(operation, ticket, row, editorTicket));
                    }
                    catch (Exception) { return Task.FromResult(false); }
                }, operation.CancellationToken);
            if (!IsRefinementOperationCurrent(operation, ticket, row, editorTicket)) return;
            var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"refinement-v1|{provider.Endpoint}|{provider.Model}")));
            await _store.CreateRefinementBatchAsync(row.Segment.Id, provider.Model, fingerprint,
                drafts, DateTimeOffset.UtcNow, operation.CancellationToken);
            if (IsRefinementOperationCurrent(operation, ticket, row, editorTicket))
            {
                QueueRefinementPanelRefresh();
                StatusText.Text = drafts.Count == 0
                    ? "El generador no propuso cambios; el original permanece intacto."
                    : "Propuestas guardadas para revisión humana; no se modificó la transcripción.";
            }
        }
        catch (OperationCanceledException)
        {
            if (IsSelectedOriginal(session.Id, row.Segment.Id)) RefinementStatusText.Text = "Análisis cancelado; no se cambió el original.";
        }
        catch (Exception)
        {
            if (IsSelectedOriginal(session.Id, row.Segment.Id)) RefinementStatusText.Text = "No se pudo completar el análisis. No se cambió el original.";
        }
        finally
        {
            _refinementOperation.Complete(operation);
            UpdateHistoryControls();
        }
    }

    private bool IsRefinementOperationCurrent(OwnedCancellationOperationCoordinator.Operation operation,
        HistoryLoadTicket ticket, HistorySegmentItem row, CorrectionEditorOperationTicket editorTicket) =>
        _refinementOperation.IsCurrent(operation) && !operation.CancellationToken.IsCancellationRequested &&
        _historyLoads.IsCurrent(ticket, SelectedHistorySession()?.Id) &&
        IsSelectedOriginal(row.Segment.SessionId, row.Segment.Id) &&
        _correctionDraftNavigation.CanReplaceEditor(editorTicket) && !HasUnsavedCorrectionDraft();

    private void CancelRefinement_Click(object sender, RoutedEventArgs e) => _refinementOperation.Cancel();

    private async void PlayRefinementAudio_Click(object sender, RoutedEventArgs e) => await PlaySelectedSegmentAsync();

    private async void AcceptRefinement_Click(object sender, RoutedEventArgs e)
    {
        if (_store is null || BlockCorrectionDraftNavigation() ||
            SelectedHistorySession() is not { } session || SelectedHistorySegment() is not { } row ||
            RefinementProposalsList.SelectedItem is not RefinementProposalItem proposal ||
            proposal.Proposal.ReviewStatus != RefinementReviewStatus.Unreviewed ||
            !IsSelectedOriginal(session.Id, row.Segment.Id)) return;
        HistoryLoadTicket ticket;
        try { ticket = _historyLoads.Capture(session.Id); }
        catch (OperationCanceledException) { return; }
        if (!_refinementOperation.TryBegin([ticket.CancellationToken, _lifetime.Token], out var operation)) return;
        var editorTicket = _correctionDraftNavigation.CaptureOperation();
        UpdateRefinementControls();
        try
        {
            var corrections = await _store.GetCorrectionsAsync(row.Segment.Id, operation.CancellationToken);
            if (!IsRefinementOperationCurrent(operation, ticket, row, editorTicket)) return;
            if ((corrections.LastOrDefault()?.Revision ?? 0) != (row.Review.LatestRevision?.Revision ?? 0))
                throw new InvalidOperationException("La revisión del segmento cambió desde que se mostró.");
            var reviewer = string.IsNullOrWhiteSpace(_settings.LocalDisplayName) ? Environment.UserName : _settings.LocalDisplayName;
            if (string.IsNullOrWhiteSpace(reviewer)) reviewer = "Usuario local";
            var accepted = await _correctionDraftNavigation.RunAsync(editorTicket,
                token => _store.AcceptRefinementProposalAsync(proposal.Proposal.Id,
                    corrections.LastOrDefault()?.Revision ?? 0, reviewer, DateTimeOffset.UtcNow, token),
                operation.CancellationToken);
            if (!IsSelectedOriginal(session.Id, row.Segment.Id)) return;
            if (!accepted.CanReplaceEditor || HasUnsavedCorrectionDraft())
            {
                RefinementStatusText.Text = "Propuesta aceptada; conserva tu borrador actual y vuelve a cargar el segmento para verla.";
                return;
            }
            await LoadHistoryReviewAsync(session, ticket, row.Segment.Id,
                editorTicket: _correctionDraftNavigation.CaptureOperation());
            await LoadPendingReviewsAsync();
            StatusText.Text = "Propuesta aceptada como corrección humana. El original permanece intacto.";
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (IsSelectedOriginal(session.Id, row.Segment.Id)) RefinementStatusText.Text = "No se pudo aceptar. Comprueba que el segmento no haya cambiado y vuelve a cargarlo.";
        }
        finally { _refinementOperation.Complete(operation); UpdateHistoryControls(); }
    }

    private async void RejectRefinement_Click(object sender, RoutedEventArgs e)
    {
        if (_store is null || BlockCorrectionDraftNavigation() ||
            SelectedHistorySession() is not { } session || SelectedHistorySegment() is not { } row ||
            RefinementProposalsList.SelectedItem is not RefinementProposalItem proposal ||
            proposal.Proposal.ReviewStatus != RefinementReviewStatus.Unreviewed ||
            !IsSelectedOriginal(session.Id, row.Segment.Id)) return;
        HistoryLoadTicket ticket;
        try { ticket = _historyLoads.Capture(session.Id); }
        catch (OperationCanceledException) { return; }
        if (!_refinementOperation.TryBegin([ticket.CancellationToken, _lifetime.Token], out var operation)) return;
        var editorTicket = _correctionDraftNavigation.CaptureOperation();
        UpdateRefinementControls();
        try
        {
            var reviewer = string.IsNullOrWhiteSpace(_settings.LocalDisplayName) ? Environment.UserName : _settings.LocalDisplayName;
            if (string.IsNullOrWhiteSpace(reviewer)) reviewer = "Usuario local";
            if (!IsRefinementOperationCurrent(operation, ticket, row, editorTicket)) return;
            await _store.ReviewRefinementProposalAsync(proposal.Proposal.Id,
                RefinementReviewStatus.Rejected, reviewer, DateTimeOffset.UtcNow,
                cancellationToken: operation.CancellationToken);
            if (IsSelectedOriginal(session.Id, row.Segment.Id))
            {
                QueueRefinementPanelRefresh();
                StatusText.Text = "Propuesta rechazada; no se modificó la transcripción.";
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (IsSelectedOriginal(session.Id, row.Segment.Id)) RefinementStatusText.Text = "No se pudo rechazar la propuesta.";
        }
        finally { _refinementOperation.Complete(operation); UpdateHistoryControls(); }
    }

    private async Task RefreshSelectedLayaEvaluationsAsync(string batchId)
    {
        var version = ++_layaEvaluationLoadVersion;
        if (_store is null) return;
        LayaEvaluationHistoryText.Text = "Cargando evaluaciones locales…";
        try
        {
            var evaluations = await _store.ListTranscriptRefinementEvaluationsAsync(batchId, _lifetime.Token);
            if (version != _layaEvaluationLoadVersion ||
                (RefinementBatchSelector.SelectedItem as RefinementBatchItem)?.Batch.Id != batchId) return;
            LayaEvaluationHistoryText.Text = LayaEvaluationPresenter.Describe(evaluations);
        }
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception)
        {
            if (version == _layaEvaluationLoadVersion)
                LayaEvaluationHistoryText.Text = "No se pudo cargar el historial de evaluaciones.";
        }
    }

    private async void EvaluateWithLaya_Click(object sender, RoutedEventArgs e)
    {
        if (_store is null || _settings.LocalLaya is not { } settings ||
            SelectedHistorySession() is not { } session || SelectedHistorySegment() is not { } row ||
            RefinementBatchSelector.SelectedItem is not RefinementBatchItem selectedBatch ||
            (selectedBatch.Batch.Proposals.Count == 0 &&
             JointQwenRevisionSelector.SelectedItem is not JointQwenRevisionItem) || HasUnsavedCorrectionDraft() ||
            !IsSelectedOriginal(session.Id, row.Segment.Id) ||
            selectedBatch.Batch.SegmentId != row.Segment.Id) return;
        try { LocalLayaSettingsPolicy.ValidateInstalled(settings); }
        catch (Exception) { RefinementStatusText.Text = "Laya no está listo: completa Node, sidecar, dependencias y modelo en Inteligencia."; return; }
        HistoryLoadTicket ticket;
        try { ticket = _historyLoads.Capture(session.Id); }
        catch (OperationCanceledException) { return; }
        if (!_refinementOperation.TryBegin([ticket.CancellationToken, _lifetime.Token], out var operation)) return;
        var editorTicket = _correctionDraftNavigation.CaptureOperation();
        var batch = selectedBatch.Batch;
        var qwenRevisionId = (JointQwenRevisionSelector.SelectedItem as JointQwenRevisionItem)?.Revision.Id;
        _jevLocalFailure = null;
        _jevFailureBatchId = null;
        UpdateRefinementControls();
        try
        {
            RefinementStatusText.Text = "Comprobando archivos y preparando la autorización de Laya…";
            var reviewed = await _store.GetReviewedSegmentsAsync(session.Id, operation.CancellationToken);
            var glossary = await _store.ListGlossaryAsync(operation.CancellationToken);
            if (!IsCurrentJointLayaEvaluation(operation, ticket, row, editorTicket, batch.Id, qwenRevisionId)) return;
            var neighbors = reviewed.Where(item => item.Segment.Id != row.Segment.Id &&
                    item.Segment.Source == row.Segment.Source &&
                    (item.IsCorrected || item.IsOriginalApproved))
                .Select(item => new TranscriptContextItem(item.Segment.Start, item.Segment.End,
                    item.EffectiveText, HumanApproved: true));
            var context = TranscriptContextPlanner.Build(neighbors, row.Segment.Start, row.Segment.End,
                glossary.Where(item => item.IsActive).Select(item => item.PreferredTerm));
            var snapshot = qwenRevisionId is null
                ? new RefinementEvaluationSnapshot(
                    batch.Id, batch.SessionId, batch.Source, batch.Start, batch.End, batch.OriginalText,
                    batch.Proposals.Select(item => new RefinementEvaluationCandidate(item.Id, item.Text)).ToArray())
                : await _store.BuildJointRefinementSnapshotAsync(batch.Id, qwenRevisionId, operation.CancellationToken);
            if (!IsCurrentJointLayaEvaluation(operation, ticket, row, editorTicket, batch.Id, qwenRevisionId)) return;
            var result = await _localLayaSidecar.EvaluateAsync(settings, snapshot, context,
                (preview, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    if (!IsCurrentJointLayaEvaluation(operation, ticket, row, editorTicket, batch.Id, qwenRevisionId))
                        return Task.FromResult(false);
                    try
                    {
                        var dialog = new LocalLayaExecutionConsentDialog(preview) { Owner = this };
                        var allowed = dialog.ShowDialog() == true &&
                            IsCurrentJointLayaEvaluation(operation, ticket, row, editorTicket, batch.Id, qwenRevisionId);
                        if (allowed) RefinementStatusText.Text = qwenRevisionId is null
                            ? "Evaluando propuestas con Laya local…"
                            : "Comparando Whisper, Qwen y propuestas con Laya local…";
                        return Task.FromResult(allowed);
                    }
                    catch (Exception) { return Task.FromResult(false); }
                }, operation.CancellationToken);
            if (!IsCurrentJointLayaEvaluation(operation, ticket, row, editorTicket, batch.Id, qwenRevisionId)) return;
            var saved = qwenRevisionId is null
                ? await _store.SaveTranscriptRefinementEvaluationAsync(batch.Id,
                    result.Evaluator, result.Judgment, DateTimeOffset.UtcNow, operation.CancellationToken)
                : await _store.SaveJointRefinementEvaluationAsync(batch.Id, qwenRevisionId, snapshot,
                    result.Evaluator, result.Judgment, DateTimeOffset.UtcNow, operation.CancellationToken);
            if (!IsCurrentJointLayaEvaluation(operation, ticket, row, editorTicket, batch.Id, qwenRevisionId)) return;
            await RefreshSelectedLayaEvaluationsAsync(batch.Id);
            RefinementStatusText.Text = "Evaluación Laya guardada: " +
                LayaEvaluationPresenter.RecommendationName(saved) +
                ". Es un juicio textual; escucha el audio y decide tú.";
        }
        catch (RefinementLayaException ex)
        {
            if (IsCurrentJointLayaEvaluation(operation, ticket, row, editorTicket, batch.Id, qwenRevisionId))
            {
                _jevLocalFailure = ex.Failure is RefinementLayaFailure.Unavailable or RefinementLayaFailure.Capacity
                    ? ex.Failure : null;
                _jevFailureBatchId = _jevLocalFailure is null ? null : batch.Id;
                RefinementStatusText.Text = _jevLocalFailure is null
                    ? "Laya no pudo evaluar este lote. Jev no se habilita por contexto excesivo, ocupación o respuesta inválida."
                    : qwenRevisionId is null
                    ? "Laya local no está disponible. Puedes solicitar manualmente el respaldo Jev tras revisar el texto que saldrá del equipo."
                    : "Laya local no está disponible. La comparación conjunta queda sin evaluar; Jev no admite Qwen en esta etapa.";
            }
        }
        catch (OperationCanceledException)
        {
            if (IsSelectedOriginal(session.Id, row.Segment.Id))
                RefinementStatusText.Text = "Evaluación Laya cancelada. Ninguna propuesta se aceptó automáticamente.";
        }
        catch (Exception ex)
        {
            if (IsSelectedOriginal(session.Id, row.Segment.Id))
                RefinementStatusText.Text = ex is TimeoutException
                    ? "Laya local no respondió a tiempo. Revisa sus archivos y la capacidad del equipo."
                    : "Laya local no pudo evaluar este lote. Revisa la instalación y el tamaño del contexto.";
        }
        finally { _refinementOperation.Complete(operation); UpdateHistoryControls(); }
    }

    private bool IsCurrentLayaEvaluation(OwnedCancellationOperationCoordinator.Operation operation,
        HistoryLoadTicket ticket, HistorySegmentItem row, CorrectionEditorOperationTicket editorTicket,
        string batchId) =>
        IsRefinementOperationCurrent(operation, ticket, row, editorTicket) &&
        (RefinementBatchSelector.SelectedItem as RefinementBatchItem)?.Batch.Id == batchId;

    private bool IsCurrentJointLayaEvaluation(OwnedCancellationOperationCoordinator.Operation operation,
        HistoryLoadTicket ticket, HistorySegmentItem row, CorrectionEditorOperationTicket editorTicket,
        string batchId, string? qwenRevisionId) =>
        IsCurrentLayaEvaluation(operation, ticket, row, editorTicket, batchId) &&
        (JointQwenRevisionSelector.SelectedItem as JointQwenRevisionItem)?.Revision.Id == qwenRevisionId;

    private void LoadJevSettingsUi()
    {
        JevApiKeyBox.Clear();
        JevSettingsStatusText.Text = _settings.JevFallback is null
            ? "Jev no configurado. No se enviará texto."
            : "Clave Jev guardada para este usuario de Windows. Guardarla no envía texto.";
        DeleteJevApiKeyButton.IsEnabled = _settings.JevFallback is not null;
        UpdateRefinementControls();
    }

    private async void SaveJevSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_closing) return;
        try
        {
            var key = JevFallbackSettingsPolicy.Create(JevApiKeyBox.Password);
            _settings = _settings with { JevFallback = key };
            await _settingsStore.SaveAsync(_settings, _lifetime.Token);
            LoadJevSettingsUi();
            StatusText.Text = "Clave Jev guardada. No se enviaron datos.";
        }
        catch (Exception)
        {
            ShowError("No se pudo guardar Jev", "Ingresa una clave válida y comprueba el acceso al almacenamiento local.");
        }
    }

    private async void DeleteJevApiKey_Click(object sender, RoutedEventArgs e)
    {
        if (_closing || _settings.JevFallback is null) return;
        if (MessageBox.Show(this, "¿Eliminar la clave Jev guardada en este equipo?",
                "Eliminar clave Jev", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try
        {
            _settings = _settings with { JevFallback = null };
            await _settingsStore.SaveAsync(_settings, _lifetime.Token);
            LoadJevSettingsUi();
            StatusText.Text = "Clave Jev eliminada.";
        }
        catch (Exception)
        {
            ShowError("No se pudo eliminar Jev", "No se pudo actualizar la configuración local.");
        }
    }

    private async void EvaluateWithJev_Click(object sender, RoutedEventArgs e)
    {
        if (_store is null || _settings.JevFallback is not { } settings ||
            SelectedHistorySession() is not { } session || SelectedHistorySegment() is not { } row ||
            RefinementBatchSelector.SelectedItem is not RefinementBatchItem selectedBatch ||
            selectedBatch.Batch.Proposals.Count is < 1 or > 2 ||
            JointQwenRevisionSelector.SelectedItem is JointQwenRevisionItem || HasUnsavedCorrectionDraft() ||
            !SegmentReviewActionsPresenter.IsSessionEligible(session.State) ||
            !IsSelectedOriginal(session.Id, row.Segment.Id) ||
            selectedBatch.Batch.SegmentId != row.Segment.Id) return;
        var batch = selectedBatch.Batch;
        var configuredLaya = _settings.LocalLaya;
        RefinementJevFallbackReason? previousReason;
        try
        {
            previousReason = RefinementJevFallbackGate.EligibleReason(configuredLaya,
                _jevFailureBatchId == batch.Id ? _jevLocalFailure : null);
        }
        catch (Exception) { return; }
        if (previousReason is null) return;
        HistoryLoadTicket ticket;
        try { ticket = _historyLoads.Capture(session.Id); }
        catch (OperationCanceledException) { return; }
        if (!_refinementOperation.TryBegin([ticket.CancellationToken, _lifetime.Token], out var operation)) return;
        var editorTicket = _correctionDraftNavigation.CaptureOperation();
        // A past failure only permits this one preflight attempt. It never authorizes a future send.
        _jevLocalFailure = null;
        _jevFailureBatchId = null;
        UpdateRefinementControls();
        try
        {
            var reviewed = await _store.GetReviewedSegmentsAsync(session.Id, operation.CancellationToken);
            var glossary = await _store.ListGlossaryAsync(operation.CancellationToken);
            if (!IsCurrentJevAttempt(operation, ticket, row, editorTicket, batch.Id, settings, configuredLaya)) return;
            var neighbors = reviewed.Where(item => item.Segment.Id != row.Segment.Id &&
                    item.Segment.Source == row.Segment.Source &&
                    (item.IsCorrected || item.IsOriginalApproved))
                .Select(item => new TranscriptContextItem(item.Segment.Start, item.Segment.End,
                    item.EffectiveText, HumanApproved: true));
            var context = TranscriptContextPlanner.Build(neighbors, row.Segment.Start, row.Segment.End,
                glossary.Where(item => item.IsActive).Select(item => item.PreferredTerm));
            var snapshot = new RefinementEvaluationSnapshot(
                batch.Id, batch.SessionId, batch.Source, batch.Start, batch.End, batch.OriginalText,
                batch.Proposals.Select(item => new RefinementEvaluationCandidate(item.Id, item.Text)).ToArray());
            RefinementStatusText.Text = "Comprobando nuevamente si Laya local puede evaluar este lote…";
            var result = await RefinementJevFallbackPreflight.RunAsync(
                configuredLaya,
                async token =>
                {
                    if (!IsCurrentJevAttempt(operation, ticket, row, editorTicket, batch.Id, settings, configuredLaya))
                        throw new OperationCanceledException(token);
                    var local = await _localLayaSidecar.EvaluateAsync(configuredLaya!, snapshot, context,
                        (preview, authorizationToken) =>
                        {
                            authorizationToken.ThrowIfCancellationRequested();
                            if (!IsCurrentJevAttempt(operation, ticket, row, editorTicket, batch.Id, settings, configuredLaya))
                                return Task.FromResult(false);
                            try
                            {
                                var dialog = new LocalLayaExecutionConsentDialog(preview) { Owner = this };
                                return Task.FromResult(dialog.ShowDialog() == true &&
                                    IsCurrentJevAttempt(operation, ticket, row, editorTicket, batch.Id, settings, configuredLaya));
                            }
                            catch (Exception) { return Task.FromResult(false); }
                        }, token);
                    if (!IsCurrentJevAttempt(operation, ticket, row, editorTicket, batch.Id, settings, configuredLaya))
                        throw new OperationCanceledException(token);
                    var savedLocal = await _store.SaveTranscriptRefinementEvaluationAsync(batch.Id,
                        local.Evaluator, local.Judgment, DateTimeOffset.UtcNow, token);
                    if (!IsCurrentJevAttempt(operation, ticket, row, editorTicket, batch.Id, settings, configuredLaya))
                        return;
                    await RefreshSelectedLayaEvaluationsAsync(batch.Id);
                    RefinementStatusText.Text = "Laya volvió a funcionar: evaluación local guardada (" +
                        LayaEvaluationPresenter.RecommendationName(savedLocal) +
                        "). No se envió ningún texto a Jev.";
                },
                (freshReason, token) =>
                {
                    if (!IsCurrentJevSend(operation, ticket, row, editorTicket, batch.Id,
                            settings, configuredLaya, freshReason))
                        throw new OperationCanceledException(token);
                    return _jevEvaluator.EvaluateAsync(snapshot, context, settings, freshReason,
                        (preview, authorizationToken) =>
                        {
                            authorizationToken.ThrowIfCancellationRequested();
                            if (!IsCurrentJevSend(operation, ticket, row, editorTicket, batch.Id,
                                    settings, configuredLaya, freshReason))
                                return Task.FromResult(false);
                            try
                            {
                                var description = preview.Reason switch
                                {
                                    RefinementJevFallbackReason.NotInstalled => "Motivo del respaldo: Laya no está instalado.",
                                    RefinementJevFallbackReason.Capacity => "Motivo del respaldo: capacidad local insuficiente.",
                                    _ => "Motivo del respaldo: Laya local no disponible."
                                };
                                var dialog = new RefinementOutboundConsentDialog(
                                    new(preview.Endpoint.AbsoluteUri, preview.Model, preview.RequestBody),
                                    description) { Owner = this };
                                return Task.FromResult(dialog.ShowDialog() == true &&
                                    IsCurrentJevSend(operation, ticket, row, editorTicket, batch.Id,
                                        settings, configuredLaya, freshReason));
                            }
                            catch (Exception) { return Task.FromResult(false); }
                        },
                        () => IsCurrentJevSend(operation, ticket, row, editorTicket, batch.Id,
                            settings, configuredLaya, freshReason), token);
                }, operation.CancellationToken);
            if (result is null) return;
            if (!IsCurrentJevSend(operation, ticket, row, editorTicket, batch.Id, settings,
                    configuredLaya, result.Evaluator.JevFallbackReason!.Value)) return;
            var saved = await _store.SaveTranscriptRefinementEvaluationAsync(batch.Id,
                result.Evaluator, result.Judgment, DateTimeOffset.UtcNow, operation.CancellationToken);
            if (!IsCurrentJevSend(operation, ticket, row, editorTicket, batch.Id, settings,
                    configuredLaya, result.Evaluator.JevFallbackReason!.Value)) return;
            await RefreshSelectedLayaEvaluationsAsync(batch.Id);
            RefinementStatusText.Text = "Evaluación Jev guardada: " +
                LayaEvaluationPresenter.RecommendationName(saved) +
                ". Es un juicio textual externo; escucha el audio y decide tú.";
        }
        catch (OperationCanceledException)
        {
            if (IsSelectedOriginal(session.Id, row.Segment.Id))
                RefinementStatusText.Text = "Evaluación cancelada; no se modificó el original.";
        }
        catch (Exception)
        {
            if (IsSelectedOriginal(session.Id, row.Segment.Id))
                RefinementStatusText.Text = "No se pudo confirmar el respaldo. No se enviará texto sin nueva autorización.";
        }
        finally { _refinementOperation.Complete(operation); UpdateHistoryControls(); }
    }

    private bool IsCurrentJevAttempt(OwnedCancellationOperationCoordinator.Operation operation,
        HistoryLoadTicket ticket, HistorySegmentItem row, CorrectionEditorOperationTicket editorTicket,
        string batchId, JevFallbackSettings settings, LocalLayaSettings? configuredLaya) =>
        IsCurrentLayaEvaluation(operation, ticket, row, editorTicket, batchId) &&
        Equals(_settings.JevFallback, settings) && Equals(_settings.LocalLaya, configuredLaya);

    private bool IsCurrentJevSend(OwnedCancellationOperationCoordinator.Operation operation,
        HistoryLoadTicket ticket, HistorySegmentItem row, CorrectionEditorOperationTicket editorTicket,
        string batchId, JevFallbackSettings settings, LocalLayaSettings? configuredLaya,
        RefinementJevFallbackReason reason)
    {
        if (!IsCurrentJevAttempt(operation, ticket, row, editorTicket, batchId, settings, configuredLaya))
            return false;
        if (reason != RefinementJevFallbackReason.NotInstalled) return true;
        try
        {
            return RefinementJevFallbackGate.EligibleReason(_settings.LocalLaya, null) ==
                RefinementJevFallbackReason.NotInstalled;
        }
        catch (Exception) { return false; }
    }
    private void UpdateRefinementControls()
    {
        if (GenerateRefinementButton is null) return;
        var session = SelectedHistorySession();
        var row = SelectedHistorySegment();
        var ready = _initialized && !_closing && !_historyDeleteInProgress && !_refinementLoading &&
            session is not null && row is not null && IsSelectedOriginal(session.Id, row.Segment.Id) &&
            SegmentReviewActionsPresenter.IsSessionEligible(session.State) && !HasUnsavedCorrectionDraft();
        var busy = _refinementOperation.IsRunning;
        var proposal = RefinementProposalsList.SelectedItem as RefinementProposalItem;
        GenerateRefinementButton.IsEnabled = ready && !busy && _settings.ExternalAiProvider is not null;
        CancelRefinementButton.IsEnabled = busy;
        PlayRefinementAudioButton.IsEnabled = ready && !busy && _historyAudio.Any(audio => audio.Source == row!.Segment.Source && audio.ChunkCount > 0);
        RefinementBatchSelector.IsEnabled = ready && !busy && _refinementBatches.Count > 0;
        RefinementProposalsList.IsEnabled = ready && !busy && _refinementProposals.Count > 0;
        JointQwenRevisionSelector.IsEnabled = ready && !busy && _jointQwenRevisions.Count > 0;
        AcceptRefinementButton.IsEnabled = ready && !busy && proposal?.Proposal.ReviewStatus == RefinementReviewStatus.Unreviewed &&
            row?.Review.IsOriginalApproved != true;
        RejectRefinementButton.IsEnabled = ready && !busy && proposal?.Proposal.ReviewStatus == RefinementReviewStatus.Unreviewed;
        var localLayaReady = false;
        try
        {
            if (_settings.LocalLaya is { } local)
            {
                LocalLayaSettingsPolicy.ValidateInstalled(local);
                localLayaReady = true;
            }
        }
        catch (Exception) { }
        EvaluateWithLayaButton.IsEnabled = ready && !busy && localLayaReady &&
            RefinementBatchSelector.SelectedItem is RefinementBatchItem &&
            (_refinementProposals.Count > 0 || JointQwenRevisionSelector.SelectedItem is JointQwenRevisionItem);
        EvaluateWithLayaButton.ToolTip = localLayaReady
            ? "Compara este análisis guardado con el modelo local. El resultado no modifica el texto."
            : "Configura Node, dependencias y modelo Laya completos en Inteligencia para habilitarlo.";
        RefinementJevFallbackReason? jevReason = null;
        try
        {
            jevReason = RefinementJevFallbackGate.EligibleReason(_settings.LocalLaya,
                (RefinementBatchSelector.SelectedItem as RefinementBatchItem)?.Batch.Id == _jevFailureBatchId
                    ? _jevLocalFailure : null);
        }
        catch (Exception) { }
        EvaluateWithJevButton.IsEnabled = ready && !busy && _settings.JevFallback is not null &&
            jevReason is not null && JointQwenRevisionSelector.SelectedItem is not JointQwenRevisionItem &&
            RefinementBatchSelector.SelectedItem is RefinementBatchItem { Batch.Proposals.Count: > 0 };
        EvaluateWithJevButton.ToolTip = JointQwenRevisionSelector.SelectedItem is JointQwenRevisionItem
            ? "Jev no evalúa la comparación conjunta con Qwen. Quita Qwen para usar el respaldo v1."
            : jevReason is null
            ? "Jev solo se habilita si Laya no está instalado o falla por capacidad o indisponibilidad."
            : _settings.JevFallback is null
                ? "Configura la clave API independiente de Jev en Inteligencia."
                : "Respaldo externo manual: revisa el JSON exacto antes de autorizar este envío.";
    }
}

internal sealed record JointQwenRevisionItem(TranscriptModelRevision Revision)
{
    public string Label => $"Qwen3-ASR configurado · {Revision.StartedAt.ToLocalTime():g} · " +
        $"{Revision.Scope?.Start}–{Revision.Scope?.End} · {Revision.ModelIdentity}";
}
