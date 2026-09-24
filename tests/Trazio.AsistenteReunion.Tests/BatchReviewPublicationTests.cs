using System.Reflection;
using System.Text.Json;
using Trazio.AsistenteReunion.App;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.Tests;

public sealed class BatchReviewPublicationTests
{
    [Fact]
    public void PendingReviewInbox_IsAccessibleSingleSelectionAndNeverAutoplays()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Trazio.AsistenteReunion.App",
            "MainWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Trazio.AsistenteReunion.App",
            "MainWindow.xaml.cs"));

        Assert.Contains("Header=\"Sesiones\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"Pendientes de revisión\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"PendingReviewList\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectionMode=\"Single\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Segmentos pendientes de revisión\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"PendingReviewStatusText\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.LiveSetting=\"Polite\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"MarkSegmentReviewedButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ReopenSegmentReviewButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DiscardCorrectionDraftButton\"", xaml, StringComparison.Ordinal);
        Assert.NotNull(typeof(MainWindow).GetMethod(
            "PendingReviewList_SelectionChanged",
            BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.NotNull(typeof(MainWindow).GetMethod(
            "MarkSegmentReviewed_Click",
            BindingFlags.Instance | BindingFlags.NonPublic));

        var selectionStart = code.IndexOf(
            "private async void PendingReviewList_SelectionChanged",
            StringComparison.Ordinal);
        var selectionEnd = code.IndexOf(
            "private async void SearchHistory_Click",
            selectionStart,
            StringComparison.Ordinal);
        var selectionPath = code[selectionStart..selectionEnd];
        Assert.Contains("_correctionDraftNavigation.CanNavigate", selectionPath, StringComparison.Ordinal);
        Assert.Contains("PendingReviewNavigationIntent.From", selectionPath, StringComparison.Ordinal);
        Assert.Contains("BeginHistoryNavigationAsync(intent)", selectionPath, StringComparison.Ordinal);
        Assert.Contains("UpdateSegmentReviewStatus();", selectionPath, StringComparison.Ordinal);
        Assert.DoesNotContain("Pendiente abierto. Revisa el texto", selectionPath, StringComparison.Ordinal);
        Assert.DoesNotContain("ApproveOriginalSegmentAsync", selectionPath, StringComparison.Ordinal);
        Assert.DoesNotContain("ReopenOriginalSegmentAsync", selectionPath, StringComparison.Ordinal);
        Assert.DoesNotContain("SaveCorrectionAsync", selectionPath, StringComparison.Ordinal);
        Assert.DoesNotContain("PlaySelectedSegmentAsync", selectionPath, StringComparison.Ordinal);
        Assert.DoesNotContain("PlayFromPositionAsync", selectionPath, StringComparison.Ordinal);

        var currentStart = code.IndexOf(
            "private bool IsCurrentHistorySearchNavigation",
            StringComparison.Ordinal);
        var currentEnd = code.IndexOf(
            "private bool IsCurrentRevisionSelection",
            currentStart,
            StringComparison.Ordinal);
        var currentPath = code[currentStart..currentEnd];
        Assert.Contains("SelectedHistorySearchNavigationKey()", currentPath, StringComparison.Ordinal);
        Assert.Contains("SelectedPendingReviewNavigationKey()", currentPath, StringComparison.Ordinal);

        var openStart = code.IndexOf(
            "private async Task OpenHistorySearchResultAsync",
            StringComparison.Ordinal);
        var openEnd = code.IndexOf(
            "private void UpdateSessionTitleEditor",
            openStart,
            StringComparison.Ordinal);
        AssertOrdered(
            code[openStart..openEnd],
            "await SelectHistorySessionAsync(storedSession, intent, ticket, editorTicket);",
            "_historyLoads.Begin(storedSession.Id);",
            "HistoryList.ScrollIntoView(item);");
    }

    [Fact]
    public void BatchApproval_IsIndependentConfirmedAtomicAndNeverCreatesCorrections()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Trazio.AsistenteReunion.App",
            "MainWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Trazio.AsistenteReunion.App",
            "MainWindow.xaml.cs"));
        var store = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Trazio.AsistenteReunion.Core",
            "SqliteBatchReviewStore.cs"));

        Assert.Contains("x:Name=\"ApproveSelectedPendingReviewsButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsChecked=\"{Binding IsBatchSelected, Mode=TwoWay", xaml, StringComparison.Ordinal);
        Assert.Contains("PreviewMouseLeftButtonDown=\"PendingReviewBatchCheckbox_PreviewMouseLeftButtonDown\"", xaml, StringComparison.Ordinal);

        var action = Slice(
            code,
            "private async Task ApproveSelectedPendingReviewsAsync",
            "private async void PendingReviewList_SelectionChanged");
        AssertOrdered(
            action,
            "MessageBox.Show(",
            "if (confirmation != MessageBoxResult.Yes) return;",
            "ApproveOriginalSegmentsAsync(",
            "_pendingReviewOperation.Complete(operation);",
            "await LoadPendingReviewsAsync();");
        Assert.Contains("La operación es atómica", action, StringComparison.Ordinal);
        Assert.DoesNotContain("SaveCorrectionAsync", action, StringComparison.Ordinal);
        Assert.DoesNotContain("AddGlossary", action, StringComparison.Ordinal);

        var storage = Slice(
            store,
            "public async Task<BatchSegmentReviewWriteResult> ApproveOriginalSegmentsAsync",
            "public async Task<IReadOnlyList<SegmentReviewDecision>> GetSegmentReviewDecisionsAsync");
        AssertOrdered(
            storage,
            "BeginTransaction(deferred: false)",
            "if (conflicts.Count > 0)",
            "RollbackAsync",
            "var decisions = new List<SegmentReviewDecision>",
            "CommitAsync");
        Assert.Contains("SegmentReviewDecisionAction.ApproveOriginal", storage, StringComparison.Ordinal);
        Assert.DoesNotContain("transcript_corrections(", storage, StringComparison.Ordinal);
    }
    [Fact]
    public void PendingReview_IsLocalBoundedAndDoesNotAddCapabilityOrAutomaticIntelligence()
    {
        var root = FindRepositoryRoot();
        var store = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Trazio.AsistenteReunion.Core",
            "SqliteBatchReviewStore.cs"));
        var capabilitiesPath = Path.Combine(
            root,
            "src",
            "Trazio.AsistenteReunion.App",
            "trazio-capabilities.json");
        using var capabilities = JsonDocument.Parse(File.ReadAllText(capabilitiesPath));

        Assert.Equal(100, PendingSegmentReviewLimits.MaximumVisibleItems);
        Assert.Contains("reviewer_nonce BLOB NOT NULL", store, StringComparison.Ordinal);
        Assert.Contains("reviewer_cipher BLOB NOT NULL", store, StringComparison.Ordinal);
        Assert.Contains("NOT EXISTS", store, StringComparison.Ordinal);
        Assert.DoesNotContain("FTS", store, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("HttpClient", store, StringComparison.Ordinal);
        Assert.DoesNotContain("Whisper", store, StringComparison.OrdinalIgnoreCase);

        var ids = capabilities.RootElement.GetProperty("capabilities")
            .EnumerateArray()
            .Select(item => item.GetProperty("id").GetString())
            .ToArray();
        Assert.Equal(5, ids.Length);
        Assert.Contains("history-review-workspace", ids);
        Assert.DoesNotContain(ids, id =>
            id?.Contains("batch", StringComparison.OrdinalIgnoreCase) == true ||
            id?.Contains("calendar", StringComparison.OrdinalIgnoreCase) == true ||
            id?.Contains("training", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public void PendingReview_LoadsAreCancelledAndDrainedBeforeDeleteOrKeyDisposal()
    {
        var root = FindRepositoryRoot();
        var code = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Trazio.AsistenteReunion.App",
            "MainWindow.xaml.cs"));

        var deleteStart = code.IndexOf("private async void Delete_Click", StringComparison.Ordinal);
        var deleteEnd = code.IndexOf("private async void ChangeStorageFolder_Click", deleteStart, StringComparison.Ordinal);
        var deletePath = code[deleteStart..deleteEnd];
        AssertOrdered(
            deletePath,
            "_historyDeleteInProgress = true;",
            "await CancelPendingReviewLoadAsync();",
            "await _audioArchive.DeleteSessionAsync(session.Id)");

        var closeStart = code.IndexOf("private async void Window_Closing", StringComparison.Ordinal);
        var closeEnd = code.IndexOf("private AppSettings ReadSettings", closeStart, StringComparison.Ordinal);
        var closePath = code[closeStart..closeEnd];
        AssertOrdered(
            closePath,
            "await CancelPendingReviewLoadAsync();",
            "_pendingReviewOperation.Dispose();",
            "_protector?.Dispose();");
    }

    [Fact]
    public void PendingReview_LoadRunsOffDispatcherAndChecksGenerationBeforePublishing()
    {
        var root = FindRepositoryRoot();
        var code = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Trazio.AsistenteReunion.App",
            "MainWindow.xaml.cs"));
        var loadStart = code.IndexOf("private async Task LoadPendingReviewsAsync", StringComparison.Ordinal);
        var loadEnd = code.IndexOf("private bool IsCurrentPendingReviewLoad", loadStart, StringComparison.Ordinal);
        var loadPath = code[loadStart..loadEnd];

        AssertOrdered(
            loadPath,
            "PendingReviewQueryDispatcher.RunAsync",
            "if (!IsCurrentPendingReviewLoad(ticket, operation)) return;",
            "var presentation = PendingReviewPresenter.Create(result);",
            "_pendingReviewRows.Clear();",
            "foreach (var item in presentation.Items) _pendingReviewRows.Add(item);");
        Assert.Contains("operation.CancellationToken", loadPath, StringComparison.Ordinal);
        Assert.Contains("[_lifetime.Token, ticket.CancellationToken]", loadPath, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "await _store.ListPendingSegmentReviewsAsync",
            loadPath,
            StringComparison.Ordinal);
    }

    [Fact]
    public void UnsavedDraft_ProtectsAllObviousSegmentSessionSourceAndViewTransitions()
    {
        var root = FindRepositoryRoot();
        var code = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Trazio.AsistenteReunion.App",
            "MainWindow.xaml.cs"));

        var segmentSelection = Slice(
            code,
            "private void HistorySegments_SelectionChanged",
            "private void PreviousHistorySegment_Click");
        AssertOrdered(
            segmentSelection,
            "RestoreCorrectionDraftSegmentSelectionIfNeeded()",
            "UpdateSelectedSegmentEditor();");

        var segmentNavigation = Slice(
            code,
            "private void NavigateHistorySegment",
            "private HistoryPlaybackNavigationState CurrentHistoryNavigation");
        AssertOrdered(
            segmentNavigation,
            "BlockCorrectionDraftNavigation()",
            "HistorySegments.SelectedItem = target;");

        var sessionSelection = Slice(
            code,
            "private async void HistoryList_SelectionChanged",
            "private async Task SelectHistorySessionAsync");
        AssertOrdered(
            sessionSelection,
            "RestoreCorrectionDraftSessionSelectionIfNeeded()",
            "HasUnsavedCorrectionDraft()",
            "var editorTicket = _correctionDraftNavigation.CaptureOperation();",
            "await CancelHistoryNavigationAsync();",
            "CanPublishCorrectionEditor(editorTicket)",
            "SelectHistorySessionAsync(session, editorTicket: editorTicket)");

        var sourceSelection = Slice(
            code,
            "private async void HistoryAudioSource_SelectionChanged",
            "private void HistorySegments_SelectionChanged");
        AssertOrdered(
            sourceSelection,
            "RestoreCorrectionDraftSourceSelectionIfNeeded()",
            "var editorTicket = _correctionDraftNavigation.CaptureOperation();",
            "CancelHistoryRetranscription();");

        var revisionSelection = Slice(
            code,
            "private async void HistoryRevisionSelector_SelectionChanged",
            "private async void Retranscribe_Click");
        AssertOrdered(
            revisionSelection,
            "HasUnsavedCorrectionDraft()",
            "HistoryRevisionSelector.SelectedIndex = 0;",
            "var editorTicket = _correctionDraftNavigation.CaptureOperation();",
            "await SupersedePlaybackAsync();");

        var historyFocus = Slice(
            code,
            "private async void HistoryTab_GotFocus",
            "private async void MainTabs_SelectionChanged");
        AssertOrdered(
            historyFocus,
            "BlockCorrectionDraftNavigation()",
            "await RefreshHistoryAsync();");

        var historyRefresh = Slice(
            code,
            "private async Task RefreshHistoryAsync",
            "private async Task OfferPendingRecoveryAsync");
        AssertOrdered(
            historyRefresh,
            "var preserveCorrectionDraft = HasUnsavedCorrectionDraft();",
            "if (suppressSelectionChanged || preserveCorrectionDraft) _suppressHistoryListSelectionChanged = true;",
            "HistoryList.ItemsSource = sessions;");

        var tabSelection = Slice(
            code,
            "private async void MainTabs_SelectionChanged",
            "private async void RefreshGlossary_Click");
        Assert.DoesNotContain("MainTabs.SelectedItem = HistoryTabItem;", tabSelection, StringComparison.Ordinal);
        Assert.DoesNotContain("UpdateSelectedSegmentEditor", tabSelection, StringComparison.Ordinal);
        Assert.DoesNotContain("_correctionDraftNavigation.Clear", tabSelection, StringComparison.Ordinal);

        var undoPath = Slice(
            code,
            "private async void UndoCorrection_Click",
            "private async void MarkSegmentReviewed_Click");
        AssertOrdered(
            undoPath,
            "BlockCorrectionDraftNavigation()",
            "UndoCorrectionAsync");

        var retranscriptionPath = Slice(
            code,
            "private async void Retranscribe_Click",
            "private void CancelRetranscription_Click");
        AssertOrdered(
            retranscriptionPath,
            "BlockCorrectionDraftNavigation()",
            "_retranscription.RunAsync");

        var stopRecordingPath = Slice(
            code,
            "private async Task StopSessionAsync",
            "private void SetRecordingButtons");
        Assert.DoesNotContain("BlockCorrectionDraftNavigation", stopRecordingPath, StringComparison.Ordinal);
        Assert.Contains("await RefreshHistoryAsync();", stopRecordingPath, StringComparison.Ordinal);

        var controls = Slice(
            code,
            "private void UpdateHistoryControls",
            "private void UpdatePendingReviewControls");
        Assert.Contains("HistoryWorkspaceTabs.IsEnabled = !playbackBlocked;", controls, StringComparison.Ordinal);
        Assert.Contains("HistorySegments.IsEnabled = !playbackBlocked && !hasUnsavedDraft;", controls, StringComparison.Ordinal);
        Assert.Contains("PreviousHistorySegmentButton.IsEnabled = !playbackBlocked && !hasUnsavedDraft", controls, StringComparison.Ordinal);
        Assert.Contains("NextHistorySegmentButton.IsEnabled = !playbackBlocked && !hasUnsavedDraft", controls, StringComparison.Ordinal);
        Assert.Contains("UndoCorrectionButton.IsEnabled = !playbackBlocked && !hasUnsavedDraft", controls, StringComparison.Ordinal);

        var closePath = Slice(
            code,
            "private async void Window_Closing",
            "private AppSettings ReadSettings");
        AssertOrdered(
            closePath,
            "if (_recording && MessageBox.Show",
            "if (HasUnsavedCorrectionDraft())",
            "Selecciona Sí para descartar el borrador y cerrar",
            "_correctionDraftNavigation.Clear();");
    }

    [Fact]
    public void CorrectionFailure_PreservesDraftAndNeverAddsGlossaryAutomatically()
    {
        var root = FindRepositoryRoot();
        var code = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Trazio.AsistenteReunion.App",
            "MainWindow.xaml.cs"));
        var saveStart = code.IndexOf("private async void SaveCorrection_Click", StringComparison.Ordinal);
        var saveEnd = code.IndexOf("private async void UndoCorrection_Click", saveStart, StringComparison.Ordinal);
        var savePath = code[saveStart..saveEnd];
        var catchStart = savePath.IndexOf("catch (Exception ex)", StringComparison.Ordinal);
        var catchPath = savePath[catchStart..];

        Assert.Contains("SaveCorrectionAsync", savePath, StringComparison.Ordinal);
        Assert.Contains("ShowError(\"No se pudo guardar la corrección\"", catchPath, StringComparison.Ordinal);
        Assert.DoesNotContain("CorrectedSegmentText", catchPath, StringComparison.Ordinal);
        Assert.DoesNotContain("_settingCorrectionEditor", catchPath, StringComparison.Ordinal);
        Assert.DoesNotContain("AddGlossaryEntry", savePath, StringComparison.Ordinal);
        Assert.DoesNotContain("AddGlossary", savePath, StringComparison.Ordinal);
    }

    [Fact]
    public void AwaitedEditorPublications_UseRevisionGuardAndSaveUsesFrozenText()
    {
        var root = FindRepositoryRoot();
        var code = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Trazio.AsistenteReunion.App",
            "MainWindow.xaml.cs"));
        var loadPath = Slice(
            code,
            "private async Task<bool> LoadHistoryReviewAsync",
            "private async Task<AnonymousVisualEvidenceProjection?> ProjectHistoryVisualEvidenceAsync");
        AssertOrdered(
            loadPath,
            "AudioWaveformBuilder.BuildAsync",
            "CanPublishCorrectionEditor(editorTicket)",
            "ApplyHistoryTrackAsync",
            "IsCorrectionEditorPublicationCurrent(editorTicket)",
            "CanPublishCorrectionEditor(editorTicket)",
            "UpdateSelectedSegmentEditor();");

        var savePath = Slice(
            code,
            "private async void SaveCorrection_Click",
            "private async void UndoCorrection_Click");
        AssertOrdered(
            savePath,
            "CaptureSave()",
            "RunSaveAsync(",
            "frozenText",
            "SaveCorrectionAsync(",
            "var advance = saveResult.Baseline;");
        Assert.DoesNotContain(
            "SaveCorrectionAsync(selected.Segment.Id, CorrectedSegmentText.Text",
            savePath,
            StringComparison.Ordinal);

        var revisionPath = Slice(
            code,
            "private async void HistoryRevisionSelector_SelectionChanged",
            "private async void Retranscribe_Click");
        AssertOrdered(
            revisionPath,
            "var editorTicket = _correctionDraftNavigation.CaptureOperation();",
            "await SupersedePlaybackAsync();",
            "CanPublishCorrectionEditor(editorTicket)",
            "UpdateSelectedSegmentEditor();");
    }

    private static void AssertOrdered(string text, params string[] values)
    {
        var previous = -1;
        foreach (var value in values)
        {
            var current = text.IndexOf(value, previous + 1, StringComparison.Ordinal);
            Assert.True(current > previous, $"No se encontró '{value}' en el orden esperado.");
            previous = current;
        }
    }

    private static string Slice(string text, string startMarker, string endMarker)
    {
        var start = text.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"No se encontró '{startMarker}'.");
        var end = text.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"No se encontró '{endMarker}' después de '{startMarker}'.");
        return text[start..end];
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Trazio.AsistenteReunion.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found from the test output directory.");
    }
}
