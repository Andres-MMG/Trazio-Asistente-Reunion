using Trazio.AsistenteReunion.App;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.Tests;

public sealed class AnonymousVisualEvidencePresentationTests
{
    [Theory]
    [Trait("Area", "VisualCapture")]
    [InlineData(AnonymousVisualCorrelationOutcome.Matched, AnonymousVisualEvidencePresentationState.Matched, "Coincidente")]
    [InlineData(AnonymousVisualCorrelationOutcome.InsufficientEvidence, AnonymousVisualEvidencePresentationState.Insufficient, "Insuficiente")]
    [InlineData(AnonymousVisualCorrelationOutcome.Unavailable, AnonymousVisualEvidencePresentationState.Unavailable, "No disponible")]
    [InlineData(AnonymousVisualCorrelationOutcome.Abstained, AnonymousVisualEvidencePresentationState.Unavailable, "No disponible")]
    public void Create_SystemOutput_MapsEveryCorrelationOutcome(
        AnonymousVisualCorrelationOutcome outcome,
        AnonymousVisualEvidencePresentationState expectedState,
        string expectedText)
    {
        var segment = Segment("session", "segment", AudioSourceKind.SystemOutput);
        var correlation = new AnonymousVisualCorrelation(
            outcome,
            outcome == AnonymousVisualCorrelationOutcome.Matched
                ? AnonymousVisualCorrelationReason.None
                : AnonymousVisualCorrelationReason.NoAvailableCoverage,
            1,
            0.5);

        var result = AnonymousVisualEvidencePresentation.Create(segment, correlation);

        Assert.Equal(expectedState, result.State);
        Assert.Equal(expectedText, result.StatusText);
        Assert.Contains("Actividad visual anónima", result.DisplayText, StringComparison.Ordinal);
        Assert.Contains("No identifica a ninguna persona", result.AutomationName, StringComparison.Ordinal);
        Assert.Contains("no identifica", result.HelpText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void Create_ValidatedIncompleteRunWithoutCoverage_IsAnalyzing()
    {
        var result = AnonymousVisualEvidencePresentation.Create(
            Segment("session", "segment", AudioSourceKind.SystemOutput),
            new AnonymousVisualCorrelation(
                AnonymousVisualCorrelationOutcome.Unavailable,
                AnonymousVisualCorrelationReason.NoAvailableCoverage,
                0,
                0),
            validatedRunIncomplete: true);

        Assert.Equal(AnonymousVisualEvidencePresentationState.Analyzing, result.State);
        Assert.Equal("Analizando", result.StatusText);
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void Create_Microphone_IsHiddenRegardlessOfCorrelation()
    {
        var result = AnonymousVisualEvidencePresentation.Create(
            Segment("session", "segment", AudioSourceKind.Microphone),
            new AnonymousVisualCorrelation(
                AnonymousVisualCorrelationOutcome.Matched,
                AnonymousVisualCorrelationReason.None,
                1,
                1));

        Assert.Same(AnonymousVisualEvidenceViewModel.Hidden, result);
        Assert.False(result.IsVisible);
        Assert.Equal(System.Windows.Visibility.Collapsed, result.Visibility);
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public async Task ProjectAsync_MicrophoneOnly_DoesNotReadStoreOrCorrelate()
    {
        var reads = 0;
        var projector = new AnonymousVisualEvidenceProjector(
            (_, _) =>
            {
                Interlocked.Increment(ref reads);
                throw new InvalidOperationException("The store must not be consulted for microphone rows.");
            },
            Policy());
        var segment = Segment("session", "microphone", AudioSourceKind.Microphone);

        var projection = await projector.ProjectAsync(
            AnonymousVisualEvidenceCacheScope.ActiveSession,
            segment.SessionId,
            [segment]);

        Assert.True(projection.IsCurrent);
        Assert.Equal(0, reads);
        Assert.Equal(AnonymousVisualEvidencePresentationState.Hidden, projection.For(segment).State);
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public async Task ProjectAsync_MissingProductionPolicy_IsUnavailable()
    {
        var segment = Segment("session", "output", AudioSourceKind.SystemOutput);
        var projector = new AnonymousVisualEvidenceProjector(
            (_, _) => Task.FromResult(MatchedEvidence(segment.SessionId)));

        var projection = await projector.ProjectAsync(
            AnonymousVisualEvidenceCacheScope.ActiveSession,
            segment.SessionId,
            [segment],
            validatedRunIncomplete: true);

        Assert.Equal(AnonymousVisualEvidencePresentationState.Unavailable, projection.For(segment).State);
        Assert.Equal("No disponible", projection.For(segment).StatusText);
    }

    [Theory]
    [Trait("Area", "VisualCapture")]
    [InlineData(AnonymousVisualEvidenceReadStatus.Corrupted)]
    [InlineData(AnonymousVisualEvidenceReadStatus.UnsupportedVersion)]
    public async Task ProjectAsync_UnreadableEvidence_IsUnavailable(
        AnonymousVisualEvidenceReadStatus status)
    {
        var segment = Segment("session", "output", AudioSourceKind.SystemOutput);
        var unreadable = status == AnonymousVisualEvidenceReadStatus.Corrupted
            ? AnonymousVisualEvidenceReadResult.Corrupted(segment.SessionId)
            : AnonymousVisualEvidenceReadResult.UnsupportedVersion(segment.SessionId);
        var projector = new AnonymousVisualEvidenceProjector(
            (_, _) => Task.FromResult(unreadable),
            Policy());

        var projection = await projector.ProjectAsync(
            AnonymousVisualEvidenceCacheScope.SelectedHistorySession,
            segment.SessionId,
            [segment]);

        Assert.Equal(AnonymousVisualEvidencePresentationState.Unavailable, projection.For(segment).State);
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public async Task ProjectAsync_ReaderFailure_IsUnavailableAndCachedForGeneration()
    {
        var reads = 0;
        var segment = Segment("session", "output", AudioSourceKind.SystemOutput);
        var projector = new AnonymousVisualEvidenceProjector(
            (_, _) =>
            {
                Interlocked.Increment(ref reads);
                throw new IOException("read failed");
            },
            Policy());

        var first = await projector.ProjectAsync(
            AnonymousVisualEvidenceCacheScope.ActiveSession,
            segment.SessionId,
            [segment]);
        var second = await projector.ProjectAsync(
            AnonymousVisualEvidenceCacheScope.ActiveSession,
            segment.SessionId,
            [segment]);

        Assert.Equal(1, reads);
        Assert.Equal(AnonymousVisualEvidencePresentationState.Unavailable, first.For(segment).State);
        Assert.Equal(AnonymousVisualEvidencePresentationState.Unavailable, second.For(segment).State);
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public async Task ProjectAsync_ManyRowsAndRepeatedProjection_ReadsOneSnapshot()
    {
        var reads = 0;
        var first = Segment("session", "first", AudioSourceKind.SystemOutput, 0, 10);
        var second = Segment("session", "second", AudioSourceKind.SystemOutput, 10, 20);
        var evidence = AnonymousVisualEvidenceReadResult.Loaded(
            first.SessionId,
            [
                Coverage(first.SessionId, 0, 20),
                Activity(first.SessionId, 0, 5),
                Activity(first.SessionId, 10, 11)
            ]);
        var projector = new AnonymousVisualEvidenceProjector(
            (_, _) =>
            {
                Interlocked.Increment(ref reads);
                return Task.FromResult(evidence);
            },
            Policy());

        var initial = await projector.ProjectAsync(
            AnonymousVisualEvidenceCacheScope.SelectedHistorySession,
            first.SessionId,
            [first, second]);
        var repeated = await projector.ProjectAsync(
            AnonymousVisualEvidenceCacheScope.SelectedHistorySession,
            first.SessionId,
            [first, second]);

        Assert.Equal(1, reads);
        Assert.Equal(AnonymousVisualEvidencePresentationState.Matched, initial.For(first).State);
        Assert.Equal(AnonymousVisualEvidencePresentationState.Insufficient, initial.For(second).State);
        Assert.Equal(initial.BySegmentId, repeated.BySegmentId);
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public async Task ProjectAsync_ConcurrentRequests_CoalesceSingleFlightRead()
    {
        var reads = 0;
        var segment = Segment("session", "output", AudioSourceKind.SystemOutput);
        var pending = new TaskCompletionSource<AnonymousVisualEvidenceReadResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var projector = new AnonymousVisualEvidenceProjector(
            (_, _) =>
            {
                Interlocked.Increment(ref reads);
                return pending.Task;
            },
            Policy());

        var first = projector.ProjectAsync(
            AnonymousVisualEvidenceCacheScope.ActiveSession,
            segment.SessionId,
            [segment]);
        var second = projector.ProjectAsync(
            AnonymousVisualEvidenceCacheScope.ActiveSession,
            segment.SessionId,
            [segment]);
        Assert.Equal(1, Volatile.Read(ref reads));

        pending.SetResult(MatchedEvidence(segment.SessionId));
        var projections = await Task.WhenAll(first, second);

        Assert.All(projections, projection => Assert.True(projection.IsCurrent));
        Assert.Equal(1, reads);
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public async Task Invalidate_WhileReadIsInFlight_SerializesFollowUpAndRejectsObsoleteSnapshot()
    {
        var segment = Segment("session", "output", AudioSourceKind.SystemOutput);
        var reads = 0;
        var firstRead = new TaskCompletionSource<AnonymousVisualEvidenceReadResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRead = new TaskCompletionSource<AnonymousVisualEvidenceReadResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var projector = new AnonymousVisualEvidenceProjector(
            (_, _) => Interlocked.Increment(ref reads) == 1 ? firstRead.Task : secondRead.Task,
            Policy());

        var obsolete = projector.ProjectAsync(
            AnonymousVisualEvidenceCacheScope.ActiveSession,
            segment.SessionId,
            [segment]);
        projector.Invalidate(AnonymousVisualEvidenceCacheScope.ActiveSession, segment.SessionId);
        var current = projector.ProjectAsync(
            AnonymousVisualEvidenceCacheScope.ActiveSession,
            segment.SessionId,
            [segment]);
        Assert.Equal(1, reads);

        firstRead.SetResult(AnonymousVisualEvidenceReadResult.Loaded(segment.SessionId, []));
        await WaitUntilAsync(() => Volatile.Read(ref reads) == 2);
        secondRead.SetResult(MatchedEvidence(segment.SessionId));
        var projections = await Task.WhenAll(obsolete, current);

        Assert.Equal(2, reads);
        Assert.All(projections, projection =>
        {
            Assert.True(projection.IsCurrent);
            Assert.True(projector.IsCurrent(projection));
            Assert.Equal(AnonymousVisualEvidencePresentationState.Matched, projection.For(segment).State);
        });
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public async Task ProjectAsync_ActiveAndHistorySlots_AreBoundedAndIndependent()
    {
        var reads = 0;
        var projector = new AnonymousVisualEvidenceProjector(
            (sessionId, _) =>
            {
                Interlocked.Increment(ref reads);
                return Task.FromResult(AnonymousVisualEvidenceReadResult.Loaded(sessionId, []));
            },
            Policy());
        var active = Segment("active", "active-segment", AudioSourceKind.SystemOutput);
        var historyA = Segment("history-a", "history-a-segment", AudioSourceKind.SystemOutput);
        var historyB = Segment("history-b", "history-b-segment", AudioSourceKind.SystemOutput);

        await projector.ProjectAsync(AnonymousVisualEvidenceCacheScope.ActiveSession, active.SessionId, [active]);
        await projector.ProjectAsync(AnonymousVisualEvidenceCacheScope.SelectedHistorySession, historyA.SessionId, [historyA]);
        await projector.ProjectAsync(AnonymousVisualEvidenceCacheScope.ActiveSession, active.SessionId, [active]);
        await projector.ProjectAsync(AnonymousVisualEvidenceCacheScope.SelectedHistorySession, historyB.SessionId, [historyB]);
        await projector.ProjectAsync(AnonymousVisualEvidenceCacheScope.SelectedHistorySession, historyA.SessionId, [historyA]);

        Assert.Equal(4, reads);
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public async Task ProjectAsync_CorrectionsAndRetranscriptionKeepTemporalCorrelationStable()
    {
        var reads = 0;
        var original = Segment("session", "original", AudioSourceKind.SystemOutput, text: "original");
        var corrected = original with { Text = "corrected" };
        var retranscribed = Segment("session", "revision", AudioSourceKind.SystemOutput, text: "new model text");
        var projector = new AnonymousVisualEvidenceProjector(
            (_, _) =>
            {
                Interlocked.Increment(ref reads);
                return Task.FromResult(MatchedEvidence(original.SessionId));
            },
            Policy());

        var originalProjection = await projector.ProjectAsync(
            AnonymousVisualEvidenceCacheScope.SelectedHistorySession,
            original.SessionId,
            [original]);
        var correctedProjection = await projector.ProjectAsync(
            AnonymousVisualEvidenceCacheScope.SelectedHistorySession,
            corrected.SessionId,
            [corrected]);
        var revisionProjection = await projector.ProjectAsync(
            AnonymousVisualEvidenceCacheScope.SelectedHistorySession,
            retranscribed.SessionId,
            [retranscribed]);

        Assert.Equal(1, reads);
        Assert.Equal(AnonymousVisualEvidencePresentationState.Matched, originalProjection.For(original).State);
        Assert.Equal(AnonymousVisualEvidencePresentationState.Matched, correctedProjection.For(corrected).State);
        Assert.Equal(AnonymousVisualEvidencePresentationState.Matched, revisionProjection.For(retranscribed).State);
        Assert.Equal("original", original.Text);
        Assert.Equal("corrected", corrected.Text);
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void RowViewModels_PreserveTranscriptAndNeverEnterExports()
    {
        var segment = Segment("session", "output", AudioSourceKind.SystemOutput, text: "transcript text");
        var review = new ReviewedTranscriptSegment(segment, null);
        var liveRow = new TranscriptRow(segment, AnonymousVisualEvidenceViewModel.Matched);
        var historyRow = new HistorySegmentItem(review, visualEvidence: AnonymousVisualEvidenceViewModel.Matched);
        var session = new SessionSummary(
            segment.SessionId,
            "Meeting",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            SessionState.Completed);

        var plainText = TranscriptExport.CreatePlainText([review]);
        var markdown = ObsidianMarkdownExport.Create(session, [review], "test");

        Assert.Same(segment, liveRow.Segment);
        Assert.Same(segment, historyRow.Segment);
        Assert.Equal("transcript text", liveRow.Text);
        Assert.Equal("transcript text", historyRow.Text);
        foreach (var evidenceText in new[] { "Actividad visual anónima", "Analizando", "Coincidente", "Insuficiente", "No disponible" })
        {
            Assert.DoesNotContain(evidenceText, plainText, StringComparison.Ordinal);
            Assert.DoesNotContain(evidenceText, markdown, StringComparison.Ordinal);
        }
    }

    private static AnonymousVisualCorrelationPolicy Policy() => new(
        MeetingProvider.GoogleMeet,
        AnonymousVisualEvidenceVersions.CurrentProfile,
        AnonymousVisualEvidenceVersions.CurrentEvidence,
        AnonymousVisualEvidenceVersions.CurrentDetector,
        AnonymousVisualEvidenceVersions.CurrentPolicy,
        MinimumConfidence: 0.5,
        MinimumCoverageRatio: 0.8,
        MinimumActivityOverlapRatio: 0.25);

    private static AnonymousVisualEvidenceReadResult MatchedEvidence(string sessionId) =>
        AnonymousVisualEvidenceReadResult.Loaded(
            sessionId,
            [Coverage(sessionId, 0, 10), Activity(sessionId, 0, 5)]);

    private static AnonymousVisualEvidenceInterval Coverage(
        string sessionId,
        int startSeconds,
        int endSeconds) =>
        AnonymousVisualEvidenceInterval.Coverage(
            Guid.NewGuid(),
            sessionId,
            TimeSpan.FromSeconds(startSeconds),
            TimeSpan.FromSeconds(endSeconds),
            AnonymousVisualAnalysisAvailability.Available,
            confidence: 1,
            Provenance());

    private static AnonymousVisualEvidenceInterval Activity(
        string sessionId,
        int startSeconds,
        int endSeconds) =>
        AnonymousVisualEvidenceInterval.Activity(
            Guid.NewGuid(),
            sessionId,
            TimeSpan.FromSeconds(startSeconds),
            TimeSpan.FromSeconds(endSeconds),
            confidence: 1,
            Provenance());

    private static AnonymousVisualEvidenceProvenance Provenance() => new(
        MeetingProvider.GoogleMeet,
        AnonymousVisualEvidenceVersions.CurrentProfile,
        AnonymousVisualEvidenceVersions.CurrentEvidence,
        AnonymousVisualEvidenceVersions.CurrentDetector,
        AnonymousVisualEvidenceVersions.CurrentPolicy);

    private static TranscriptSegment Segment(
        string sessionId,
        string id,
        AudioSourceKind source,
        int startSeconds = 0,
        int endSeconds = 10,
        string text = "text") =>
        new(
            id,
            sessionId,
            source,
            0,
            TimeSpan.FromSeconds(startSeconds),
            TimeSpan.FromSeconds(endSeconds),
            text,
            DateTimeOffset.UtcNow);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= timeout)
                throw new TimeoutException("The expected asynchronous condition was not reached.");
            await Task.Delay(10);
        }
    }
}
