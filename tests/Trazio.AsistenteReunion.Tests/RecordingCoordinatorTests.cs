using System.Security.Cryptography;
using System.Diagnostics;
using Trazio.AsistenteReunion.App;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.Tests;

public sealed class RecordingCoordinatorTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "trazio-coordinator-" + Guid.NewGuid().ToString("N"));
    private readonly string _modelPath;
    private AesContentProtector _protector = null!;
    private SqliteSessionStore _store = null!;

    public RecordingCoordinatorTests() => _modelPath = Path.Combine(_directory, "model.bin");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllBytesAsync(_modelPath, [1]);
        _protector = new(RandomNumberGenerator.GetBytes(32));
        _store = new(Path.Combine(_directory, "test.db"), _protector);
        await _store.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        _protector.Dispose();
        Directory.Delete(_directory, true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task StopAsync_WithAcceptedTail_DrainsPersistsAndCompletesSession()
    {
        var capture = new FakeAudioCapture();
        var factory = new FakeTransportFactory();
        await using var coordinator = new RecordingCoordinator(capture, _store, new PendingAudioQueue(), factory);
        await coordinator.StartAsync("Tail drain", Settings());
        capture.Emit(AudioSourceKind.Microphone, [1, 2, 3, 4]);
        capture.Emit(AudioSourceKind.Microphone, [5, 6, 7, 8]);

        await coordinator.StopAsync();

        var session = Assert.Single(await _store.ListSessionsAsync());
        Assert.Equal(SessionState.Completed, session.State);
        Assert.Single(await _store.GetSegmentsAsync(session.Id));
        Assert.Empty(await _store.GetPendingAsync(session.Id));
        Assert.Equal(1, factory.Transports.Single().TranscriptionCount);
    }

    [Fact]
    public async Task StopAsync_DuringDequeueHandoff_WaitsForLeasedChunk()
    {
        var handoffReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHandoff = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new PendingAudioQueue(afterLeaseTransfer: async cancellationToken =>
        {
            handoffReached.TrySetResult();
            await releaseHandoff.Task.WaitAsync(cancellationToken);
        });
        var capture = new FakeAudioCapture();
        await using var coordinator = new RecordingCoordinator(capture, _store, queue, new FakeTransportFactory());
        await coordinator.StartAsync("Lease handoff", Settings());
        capture.Emit(AudioSourceKind.Microphone, [1, 2, 3, 4]);
        await handoffReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, queue.LeasedCount);
        Assert.Equal(1, queue.OutstandingCount);

        var stopTask = coordinator.StopAsync();
        await Task.Delay(100);
        Assert.False(stopTask.IsCompleted);

        releaseHandoff.TrySetResult();
        await stopTask.WaitAsync(TimeSpan.FromSeconds(5));
        var session = Assert.Single(await _store.ListSessionsAsync());
        Assert.Equal(SessionState.Completed, session.State);
        Assert.Empty(await _store.GetPendingAsync(session.Id));
        Assert.Equal(0, queue.OutstandingCount);
    }

    [Fact]
    public async Task StartAsync_WhenCaptureStartupFails_RollsBackAndCanRetry()
    {
        var capture = new FakeAudioCapture { StartFailuresRemaining = 1 };
        var factory = new FakeTransportFactory();
        await using var coordinator = new RecordingCoordinator(capture, _store, new PendingAudioQueue(), factory);

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.StartAsync("Failed", Settings()));
        Assert.Empty(await _store.ListSessionsAsync());
        Assert.Null(coordinator.ActiveTimelineContext);

        await coordinator.StartAsync("Retry", Settings());
        capture.Emit(AudioSourceKind.Microphone, [1, 2]);
        await coordinator.StopAsync();

        var session = Assert.Single(await _store.ListSessionsAsync());
        Assert.Equal("Retry", session.Title);
        Assert.Equal(SessionState.Completed, session.State);
        Assert.Equal(2, capture.StartCount);
        Assert.True(capture.StopCount >= 2);
    }

    [Fact]
    public async Task StartAsync_CreatesTimelineFromSessionStartAndStopRevokesItsContext()
    {
        var startedAt = new DateTimeOffset(2026, 9, 23, 16, 0, 0, TimeSpan.Zero);
        var time = new ManualTimeProvider(startedAt);
        var capture = new FakeAudioCapture();
        await using var coordinator = new RecordingCoordinator(
            capture,
            _store,
            new PendingAudioQueue(),
            new FakeTransportFactory(),
            timeProvider: time);

        await coordinator.StartAsync("Timeline", Settings());
        var session = Assert.Single(await _store.ListSessionsAsync());
        var context = Assert.IsType<SessionTimelineContext>(coordinator.ActiveTimelineContext);

        Assert.Equal(session.Id, context.SessionId);
        Assert.Equal(startedAt, session.StartedAt);
        Assert.Equal(session.StartedAt, context.StartedAtUtc);
        Assert.True(context.TryGetCurrentOffset(out var initialOffset));
        Assert.Equal(TimeSpan.Zero, initialOffset);

        await coordinator.StopAsync();

        Assert.Null(coordinator.ActiveTimelineContext);
        Assert.False(context.TryGetCurrentOffset(out _));
    }

    [Fact]
    public async Task OnSecondCaptured_WhenLevelChangedBlocks_PreservesCallbackEntryTimestampAndPcm()
    {
        var startedAt = new DateTimeOffset(2026, 9, 23, 17, 0, 0, TimeSpan.Zero);
        var time = new ManualTimeProvider(startedAt);
        var capture = new FakeAudioCapture();
        var factory = new FakeTransportFactory();
        await using var coordinator = new RecordingCoordinator(
            capture,
            _store,
            new PendingAudioQueue(),
            factory,
            timeProvider: time);
        var levelEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLevel = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.LevelChanged += (_, _) =>
        {
            levelEntered.TrySetResult();
            releaseLevel.Task.GetAwaiter().GetResult();
        };
        await coordinator.StartAsync("Blocked level", Settings());
        time.AdvanceTimestamp(TimeSpan.FromSeconds(2));
        byte[] pcm = [1, 2, 3, 4];

        var emitting = Task.Run(() => capture.Emit(AudioSourceKind.Microphone, pcm));
        await levelEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        time.AdvanceTimestamp(TimeSpan.FromSeconds(30));
        releaseLevel.TrySetResult();
        await emitting;
        await coordinator.StopAsync();

        var session = Assert.Single(await _store.ListSessionsAsync());
        var segment = Assert.Single(await _store.GetSegmentsAsync(session.Id));
        Assert.Equal(TimeSpan.FromSeconds(2), segment.Start);
        Assert.Equal(pcm, Assert.Single(factory.Transports).TranscribedPcm.Single());
    }

    [Fact]
    public async Task IngestAsync_WhenQueueingOccursAfterClockAdvance_UsesEnvelopedOffset()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 9, 23, 18, 0, 0, TimeSpan.Zero));
        var capture = new FakeAudioCapture();
        await using var coordinator = new RecordingCoordinator(
            capture,
            _store,
            new PendingAudioQueue(),
            new FakeTransportFactory(),
            timeProvider: time);
        coordinator.LevelChanged += (_, _) => time.AdvanceTimestamp(TimeSpan.FromSeconds(45));
        await coordinator.StartAsync("Queued timestamp", Settings());
        time.AdvanceTimestamp(TimeSpan.FromSeconds(3));

        capture.Emit(AudioSourceKind.Microphone, [5, 6]);
        await coordinator.StopAsync();

        var session = Assert.Single(await _store.ListSessionsAsync());
        Assert.Equal(TimeSpan.FromSeconds(3), Assert.Single(await _store.GetSegmentsAsync(session.Id)).Start);
    }

    [Fact]
    public async Task ConsecutiveSessions_UseDistinctRevocableTimelinesWithFreshOffsets()
    {
        var firstStart = new DateTimeOffset(2026, 9, 23, 19, 0, 0, TimeSpan.Zero);
        var time = new ManualTimeProvider(firstStart);
        var capture = new FakeAudioCapture();
        await using var coordinator = new RecordingCoordinator(
            capture,
            _store,
            new PendingAudioQueue(),
            new FakeTransportFactory(),
            timeProvider: time);
        await coordinator.StartAsync("First timeline", Settings());
        var firstContext = Assert.IsType<SessionTimelineContext>(coordinator.ActiveTimelineContext);
        time.AdvanceTimestamp(TimeSpan.FromSeconds(2));
        capture.Emit(AudioSourceKind.Microphone, [1, 2]);
        await coordinator.StopAsync();

        var secondStart = firstStart.AddHours(1);
        time.SetUtcNow(secondStart);
        await coordinator.StartAsync("Second timeline", Settings());
        var secondContext = Assert.IsType<SessionTimelineContext>(coordinator.ActiveTimelineContext);
        time.AdvanceTimestamp(TimeSpan.FromSeconds(1));
        capture.Emit(AudioSourceKind.Microphone, [3, 4]);
        await coordinator.StopAsync();

        Assert.NotSame(firstContext, secondContext);
        Assert.NotEqual(firstContext.SessionId, secondContext.SessionId);
        Assert.True(secondContext.Revision > firstContext.Revision);
        Assert.False(firstContext.TryGetCurrentOffset(out _));
        Assert.False(secondContext.TryGetCurrentOffset(out _));
        var sessions = await _store.ListSessionsAsync();
        var first = sessions.Single(session => session.Title == "First timeline");
        var second = sessions.Single(session => session.Title == "Second timeline");
        Assert.Equal(firstStart, first.StartedAt);
        Assert.Equal(secondStart, second.StartedAt);
        Assert.Equal(TimeSpan.FromSeconds(2), Assert.Single(await _store.GetSegmentsAsync(first.Id)).Start);
        Assert.Equal(TimeSpan.FromSeconds(1), Assert.Single(await _store.GetSegmentsAsync(second.Id)).Start);
    }

    [Fact]
    public void CapturePacketContinuity_LongSilentGapRotatesOnlyWithoutPartialAudio()
    {
        const int bytesPerSecond = 32_000;
        Assert.Equal(CaptureGapAction.Continue,
            CapturePacketContinuity.Decide(TimeSpan.FromMilliseconds(1008), 32_000, 32_000, bytesPerSecond, 0));
        Assert.Equal(CaptureGapAction.Rotate,
            CapturePacketContinuity.Decide(TimeSpan.FromSeconds(61), 32_000, 32_000, bytesPerSecond, 0));
        Assert.Equal(CaptureGapAction.Fail,
            CapturePacketContinuity.Decide(TimeSpan.FromSeconds(61), 16_000, 16_000, bytesPerSecond, 16_000));
        Assert.Equal(CaptureGapAction.Continue,
            CapturePacketContinuity.Decide(TimeSpan.FromMilliseconds(508), 16_000, 16_000, bytesPerSecond, 16_000));
        Assert.Equal(CaptureGapAction.Continue,
            CapturePacketContinuity.Decide(TimeSpan.FromMilliseconds(108), 3_200, 3_200, bytesPerSecond, 0));
        Assert.Equal(CaptureGapAction.Continue,
            CapturePacketContinuity.Decide(TimeSpan.FromMilliseconds(92), 3_200, 3_200, bytesPerSecond, 0));
        Assert.Equal(CaptureGapAction.Rotate,
            CapturePacketContinuity.Decide(TimeSpan.FromMilliseconds(400), 3_200, 3_200, bytesPerSecond, 0));
        Assert.Equal(CaptureGapAction.Fail,
            CapturePacketContinuity.Decide(TimeSpan.FromMilliseconds(400), 3_200, 3_200, bytesPerSecond, 1_600));
        Assert.Equal(CaptureGapAction.Rotate,
            CapturePacketContinuity.Decide(TimeSpan.FromMilliseconds(400), 32_000, 3_200, bytesPerSecond, 0));
    }

    [Fact]
    public async Task StampedPcm_PublishedAfterNewCaptureRun_PreservesOriginalTimeAndRun()
    {
        var start = new DateTimeOffset(2026, 9, 24, 9, 0, 0, TimeSpan.Zero);
        var time = new ManualTimeProvider(start);
        var capture = new FakeAudioCapture();
        var archive = new AudioArchiveStore(Path.Combine(_directory, "stamped-audio"), _store, _protector);
        await using var coordinator = new RecordingCoordinator(
            capture, _store, new PendingAudioQueue(), new FakeTransportFactory(), archive, time);
        await coordinator.StartAsync("Stamped PCM", Settings() with { KeepEncryptedAudio = true });

        time.AdvanceTimestamp(TimeSpan.FromSeconds(1));
        capture.EmitStamped(AudioSourceKind.Microphone, new byte[32_000], "old-run", 0,
            Stopwatch.GetTimestamp() - Stopwatch.Frequency);
        time.AdvanceTimestamp(TimeSpan.FromSeconds(1));
        capture.EmitStamped(AudioSourceKind.Microphone, new byte[32_000], "new-run", 16_000,
            Stopwatch.GetTimestamp() - Stopwatch.Frequency);
        await coordinator.StopAsync();

        var session = Assert.Single(await _store.ListSessionsAsync());
        var chunks = await _store.GetArchivedAudioAsync(session.Id, AudioSourceKind.Microphone);
        Assert.Equal(2, chunks.Count);
        Assert.Equal("old-run", chunks[0].CaptureRunId);
        Assert.Equal("new-run", chunks[1].CaptureRunId);
        Assert.InRange(chunks[0].StartedAt - start, TimeSpan.Zero, TimeSpan.FromMilliseconds(40));
        Assert.InRange(chunks[1].StartedAt - start, TimeSpan.FromMilliseconds(960), TimeSpan.FromMilliseconds(1_040));
    }

    [Fact]
    public async Task CallbackJitter_LaterWhisperWindowAndArchiveShareSampleClock()
    {
        var start = new DateTimeOffset(2026, 9, 24, 10, 0, 0, TimeSpan.Zero);
        var time = new ManualTimeProvider(start);
        var capture = new FakeAudioCapture();
        var archive = new AudioArchiveStore(Path.Combine(_directory, "audio"), _store, _protector);
        var factory = new FakeTransportFactory { SegmentStartMilliseconds = 1_000 };
        await using var coordinator = new RecordingCoordinator(
            capture, _store, new PendingAudioQueue(), factory, archive, time);
        await coordinator.StartAsync("Clock", Settings() with { KeepEncryptedAudio = true });
        var previousTicks = 0L;
        for (var second = 0; second < 16; second++)
        {
            var targetTicks = TimeSpan.FromSeconds(second).Ticks +
                (second == 0 ? 0 : second % 2 == 0 ? TimeSpan.FromMilliseconds(-6).Ticks : TimeSpan.FromMilliseconds(8).Ticks);
            time.AdvanceTimestamp(TimeSpan.FromTicks(targetTicks - previousTicks));
            previousTicks = targetTicks;
            capture.EmitWithMetadata(AudioSourceKind.Microphone, new byte[32_000], "run-a", second * 16_000L);
            await Task.Delay(25);
        }
        await coordinator.StopAsync();

        var session = Assert.Single(await _store.ListSessionsAsync());
        var segments = await _store.GetSegmentsAsync(session.Id);
        Assert.Contains(segments, item => item.Start == TimeSpan.FromSeconds(15));
        var chunk = Assert.Single(await _store.GetArchivedAudioAsync(session.Id, AudioSourceKind.Microphone));
        Assert.Equal(start, chunk.StartedAt);
        Assert.Equal(TimeSpan.FromSeconds(16), chunk.Duration);
        Assert.Equal(0, chunk.ContinuityEpoch);
    }

    [Fact]
    public async Task LoopbackSilence_SplitsWhisperAndArchiveWithoutAssigningOldAudioToNewTime()
    {
        var start = new DateTimeOffset(2026, 9, 24, 11, 0, 0, TimeSpan.Zero);
        var time = new ManualTimeProvider(start);
        var capture = new FakeAudioCapture();
        var archive = new AudioArchiveStore(Path.Combine(_directory, "audio"), _store, _protector);
        var factory = new FakeTransportFactory();
        await using var coordinator = new RecordingCoordinator(
            capture, _store, new PendingAudioQueue(), factory, archive, time);
        await coordinator.StartAsync("Silence", Settings() with { CaptureSystemOutput = true, KeepEncryptedAudio = true });
        capture.EmitWithMetadata(AudioSourceKind.SystemOutput, Enumerable.Repeat((byte)0x11, 32_000).ToArray(), "run-a", 0);
        time.AdvanceTimestamp(TimeSpan.FromSeconds(61));
        capture.EmitWithMetadata(AudioSourceKind.SystemOutput, Enumerable.Repeat((byte)0x22, 32_000).ToArray(), "run-a", 16_000);
        await coordinator.StopAsync();

        var session = Assert.Single(await _store.ListSessionsAsync());
        var chunks = await _store.GetArchivedAudioAsync(session.Id, AudioSourceKind.SystemOutput);
        Assert.Equal(2, chunks.Count);
        Assert.Equal(start, chunks[0].StartedAt);
        Assert.Equal(start.AddSeconds(61), chunks[1].StartedAt);
        Assert.NotEqual(chunks[0].ContinuityEpoch, chunks[1].ContinuityEpoch);
        var segments = await _store.GetSegmentsAsync(session.Id);
        Assert.Contains(segments, item => item.Start == TimeSpan.Zero);
        Assert.Contains(segments, item => item.Start == TimeSpan.FromSeconds(61));
        Assert.Equal(2, factory.Transports.Single().TranscribedPcm.Count);
        var later = await RetainedAudioInterval.ReadExactAsync(archive, session,
            AudioSourceKind.SystemOutput, chunks, TimeSpan.FromSeconds(61), TimeSpan.FromSeconds(62), CancellationToken.None);
        Assert.All(later, value => Assert.Equal((byte)0x22, value));
        await Assert.ThrowsAsync<InvalidOperationException>(() => RetainedAudioInterval.ReadExactAsync(archive, session,
            AudioSourceKind.SystemOutput, chunks, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), CancellationToken.None));
    }

    [Fact]
    public async Task RecoveryWithoutSampleProvenance_NeverCombinesChunksAcrossUnknownGap()
    {
        var start = DateTimeOffset.UtcNow.AddMinutes(-2);
        var session = new MeetingSession("recover-gap", "Recover", start, start.AddSeconds(62), SessionState.Interrupted);
        await _store.CreateSessionAsync(session);
        var first = AudioChunk.Create(session.Id, AudioSourceKind.SystemOutput, 0, start, new byte[32_000]);
        var later = AudioChunk.Create(session.Id, AudioSourceKind.SystemOutput, 1, start.AddSeconds(61), new byte[32_000]);
        await _store.SavePendingAsync(first, DateTimeOffset.UtcNow.AddHours(1));
        await _store.SavePendingAsync(later, DateTimeOffset.UtcNow.AddHours(1));
        var capture = new FakeAudioCapture();
        var factory = new FakeTransportFactory();
        await using var coordinator = new RecordingCoordinator(capture, _store, new PendingAudioQueue(), factory);
        await coordinator.RecoverAsync(new SessionSummary(session.Id, session.Title, session.StartedAt,
            session.EndedAt, session.State), Settings(), [first, later]);

        Assert.Equal(2, factory.Transports.Single().TranscribedPcm.Count);
        var segments = await _store.GetSegmentsAsync(session.Id);
        Assert.Contains(segments, item => item.Start == TimeSpan.Zero);
        Assert.Contains(segments, item => item.Start == TimeSpan.FromSeconds(61));
    }

    [Fact]
    public async Task PauseResume_FlushesArchivedPartialChunkAndChangesAdmissionEpoch()
    {
        var capture = new FakeAudioCapture();
        var archive = new AudioArchiveStore(Path.Combine(_directory, "audio"), _store, _protector);
        await using var coordinator = new RecordingCoordinator(
            capture, _store, new PendingAudioQueue(), new FakeTransportFactory(), archive);
        await coordinator.StartAsync("Epoch", Settings() with { KeepEncryptedAudio = true });
        capture.EmitWithMetadata(AudioSourceKind.Microphone, new byte[32_000], "run-a", 0);
        coordinator.Pause();
        capture.EmitWithMetadata(AudioSourceKind.Microphone, new byte[32_000], "run-a", 16_000);
        coordinator.Resume();
        capture.EmitWithMetadata(AudioSourceKind.Microphone, new byte[32_000], "run-a", 32_000);
        await coordinator.StopAsync();

        var session = Assert.Single(await _store.ListSessionsAsync());
        var chunks = await _store.GetArchivedAudioAsync(session.Id, AudioSourceKind.Microphone);
        Assert.Equal(2, chunks.Count);
        Assert.Equal(0, chunks[0].ContinuityEpoch);
        Assert.Equal(2, chunks[1].ContinuityEpoch);
        Assert.Equal(32_000, chunks[1].FirstSourceSample);
        Assert.Equal(TimeSpan.FromSeconds(1), chunks[0].Duration);
        Assert.Equal(TimeSpan.FromSeconds(1), chunks[1].Duration);
    }

    [Fact]
    public async Task PauseResume_PreservesTimelineContinuityAndIgnoresPausedAudio()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 9, 23, 20, 0, 0, TimeSpan.Zero));
        var capture = new FakeAudioCapture();
        await using var coordinator = new RecordingCoordinator(
            capture,
            _store,
            new PendingAudioQueue(),
            new FakeTransportFactory(),
            timeProvider: time);
        await coordinator.StartAsync("Pause continuity", Settings() with { CaptureSystemOutput = true });
        time.AdvanceTimestamp(TimeSpan.FromSeconds(1));
        capture.Emit(AudioSourceKind.Microphone, [1, 2]);
        coordinator.Pause();
        time.AdvanceTimestamp(TimeSpan.FromSeconds(5));
        capture.Emit(AudioSourceKind.SystemOutput, [3, 4]);
        time.AdvanceTimestamp(TimeSpan.FromSeconds(4));
        coordinator.Resume();
        capture.Emit(AudioSourceKind.SystemOutput, [5, 6]);

        await coordinator.StopAsync();

        var session = Assert.Single(await _store.ListSessionsAsync());
        var segments = await _store.GetSegmentsAsync(session.Id);
        Assert.Equal(2, segments.Count);
        Assert.Equal(TimeSpan.FromSeconds(1), Assert.Single(segments, item => item.Source == AudioSourceKind.Microphone).Start);
        Assert.Equal(TimeSpan.FromSeconds(10), Assert.Single(segments, item => item.Source == AudioSourceKind.SystemOutput).Start);
    }

    [Fact]
    public async Task OnSecondCaptured_WhenResumeOvertakesPausedCallback_DoesNotAdmitAudioRetroactively()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 9, 23, 20, 30, 0, TimeSpan.Zero));
        var capture = new FakeAudioCapture();
        var factory = new FakeTransportFactory();
        await using var coordinator = new RecordingCoordinator(
            capture,
            _store,
            new PendingAudioQueue(),
            factory,
            timeProvider: time);
        var levelEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLevel = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackCount = 0;
        coordinator.LevelChanged += (_, _) =>
        {
            if (Interlocked.Increment(ref callbackCount) != 1) return;
            levelEntered.TrySetResult();
            releaseLevel.Task.GetAwaiter().GetResult();
        };
        await coordinator.StartAsync("Paused callback", Settings());
        coordinator.Pause();
        time.AdvanceTimestamp(TimeSpan.FromSeconds(2));

        var pausedEmission = Task.Run(() => capture.Emit(AudioSourceKind.Microphone, [1, 2]));
        await levelEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Run(coordinator.Resume);
        releaseLevel.TrySetResult();
        await pausedEmission;

        time.AdvanceTimestamp(TimeSpan.FromSeconds(1));
        capture.Emit(AudioSourceKind.Microphone, [3, 4]);
        await coordinator.StopAsync();

        var session = Assert.Single(await _store.ListSessionsAsync());
        var segment = Assert.Single(await _store.GetSegmentsAsync(session.Id));
        Assert.Equal(TimeSpan.FromSeconds(3), segment.Start);
        Assert.Equal([3, 4], Assert.Single(factory.Transports).TranscribedPcm.Single());
    }

    [Fact]
    public async Task OnSecondCaptured_WhenStopAndNextStartOvertakeBlockedCallback_RejectsStaleEnvelope()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 9, 23, 21, 0, 0, TimeSpan.Zero));
        var capture = new FakeAudioCapture();
        var factory = new FakeTransportFactory();
        await using var coordinator = new RecordingCoordinator(
            capture,
            _store,
            new PendingAudioQueue(),
            factory,
            timeProvider: time);
        var levelEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLevel = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackCount = 0;
        coordinator.LevelChanged += (_, _) =>
        {
            if (Interlocked.Increment(ref callbackCount) != 1) return;
            levelEntered.TrySetResult();
            releaseLevel.Task.GetAwaiter().GetResult();
        };
        await coordinator.StartAsync("Raced first", Settings());
        var staleContext = Assert.IsType<SessionTimelineContext>(coordinator.ActiveTimelineContext);
        var staleEmission = Task.Run(() => capture.Emit(AudioSourceKind.Microphone, [1, 2]));
        await levelEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await coordinator.StopAsync();
        time.SetUtcNow(time.GetUtcNow().AddMinutes(1));
        await coordinator.StartAsync("Raced second", Settings());
        releaseLevel.TrySetResult();
        await staleEmission;
        time.AdvanceTimestamp(TimeSpan.FromSeconds(1));
        capture.Emit(AudioSourceKind.Microphone, [3, 4]);
        await coordinator.StopAsync();

        Assert.False(staleContext.TryGetCurrentOffset(out _));
        var second = (await _store.ListSessionsAsync()).Single(session => session.Title == "Raced second");
        Assert.Single(await _store.GetSegmentsAsync(second.Id));
        Assert.Equal([3, 4], factory.Transports[^1].TranscribedPcm.Single());
    }

    [Fact]
    public async Task CaptureFailure_RevokesTimelineDisablesResumeAndPreservesAudioAsInterrupted()
    {
        var capture = new FakeAudioCapture();
        var archive = new AudioArchiveStore(Path.Combine(_directory, "audio"), _store, _protector);
        await using var coordinator = new RecordingCoordinator(
            capture, _store, new PendingAudioQueue(), new FakeTransportFactory(), archive,
            new ManualTimeProvider(DateTimeOffset.UtcNow));
        var statuses = new List<string>();
        coordinator.StatusChanged += (_, status) => statuses.Add(status);
        await coordinator.StartAsync("Capture failure", Settings() with { KeepEncryptedAudio = true });
        var context = Assert.IsType<SessionTimelineContext>(coordinator.ActiveTimelineContext);
        capture.EmitWithMetadata(AudioSourceKind.Microphone, new byte[32_000], "run-a", 0);
        capture.Fail("partial audio cannot be dated");
        var statusAfterFailure = statuses[^1];

        coordinator.Resume();

        Assert.Null(coordinator.ActiveTimelineContext);
        Assert.False(context.TryGetCurrentOffset(out _));
        Assert.Equal(statusAfterFailure, statuses[^1]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.StopAsync());
        var session = Assert.Single(await _store.ListSessionsAsync());
        Assert.Equal(SessionState.Interrupted, session.State);
        Assert.Single(await _store.GetArchivedAudioAsync(session.Id, AudioSourceKind.Microphone));
    }

    [Fact]
    public async Task StartAsync_WhenWorkerStartupFails_DeletesEmptySessionAndCanRetry()
    {
        var capture = new FakeAudioCapture();
        var factory = new FakeTransportFactory { StartFailuresRemaining = 1 };
        await using var coordinator = new RecordingCoordinator(capture, _store, new PendingAudioQueue(), factory);

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.StartAsync("Failed", Settings()));
        Assert.Empty(await _store.ListSessionsAsync());

        await coordinator.StartAsync("Retry worker", Settings());
        capture.Emit(AudioSourceKind.Microphone, [1, 2]);
        await coordinator.StopAsync();
        Assert.Equal(SessionState.Completed, Assert.Single(await _store.ListSessionsAsync()).State);
    }

    [Fact]
    public async Task StopAsync_WhenAudioOptedIn_FlushesEncryptedTailBySource()
    {
        var capture = new FakeAudioCapture();
        var archive = new AudioArchiveStore(Path.Combine(_directory, "audio"), _store, _protector);
        await using var coordinator = new RecordingCoordinator(capture, _store, new PendingAudioQueue(), new FakeTransportFactory(), archive);
        await coordinator.StartAsync("Archived", Settings() with { KeepEncryptedAudio = true });
        capture.Emit(AudioSourceKind.Microphone, new byte[32_000]);
        await coordinator.StopAsync();

        var session = Assert.Single(await _store.ListSessionsAsync());
        var chunk = Assert.Single(await _store.GetArchivedAudioAsync(session.Id));
        Assert.Equal(AudioSourceKind.Microphone, chunk.Source);
        Assert.Equal(TimeSpan.FromSeconds(1), chunk.Duration);
    }

    [Fact]
    public async Task StartAsync_OldSessionCallback_IsRejectedByRevision()
    {
        var capture = new FakeAudioCapture();
        await using var coordinator = new RecordingCoordinator(capture, _store, new PendingAudioQueue(), new FakeTransportFactory());
        await coordinator.StartAsync("First", Settings());
        var stale = capture.SnapshotHandler();
        var staleFailure = capture.SnapshotFailureHandler();
        await coordinator.StopAsync();
        await coordinator.StartAsync("Second", Settings());
        stale(capture, new(AudioSourceKind.Microphone, [1, 2], 0.4f));
        staleFailure(capture, "old device failure");
        capture.Emit(AudioSourceKind.Microphone, [3, 4]);
        await coordinator.StopAsync();

        var second = (await _store.ListSessionsAsync()).Single(s => s.Title == "Second");
        Assert.Single(await _store.GetSegmentsAsync(second.Id));
    }

    [Fact]
    public async Task StopAsync_AfterConsumerFailure_CancelsAndAwaitsDiagnosticPump()
    {
        var capture = new FakeAudioCapture();
        var factory = new FakeTransportFactory { ThrowOnTranscribe = true };
        await using var coordinator = new RecordingCoordinator(capture, _store, new PendingAudioQueue(), factory);
        var diagnostics = 0;
        coordinator.DiagnosticChanged += (_, _) => Interlocked.Increment(ref diagnostics);
        await coordinator.StartAsync("Failure", Settings());
        capture.Emit(AudioSourceKind.Microphone, [1, 2]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.StopAsync());
        var afterStop = Volatile.Read(ref diagnostics);
        await Task.Delay(2200);
        Assert.Equal(afterStop, Volatile.Read(ref diagnostics));
    }

    [Fact]
    public async Task StartAsync_WithMeetingOverride_AttributesOnlyMicrophoneSegments()
    {
        var capture = new FakeAudioCapture();
        await using var coordinator = new RecordingCoordinator(capture, _store, new PendingAudioQueue(), new FakeTransportFactory());
        var settings = Settings() with { CaptureSystemOutput = true };
        await coordinator.StartAsync("Attributed", settings, localDisplayNameOverride: "Meeting Guest");
        capture.Emit(AudioSourceKind.Microphone, [1, 2]);
        capture.Emit(AudioSourceKind.SystemOutput, [3, 4]);

        await coordinator.StopAsync();

        var session = Assert.Single(await _store.ListSessionsAsync());
        var segments = await _store.GetSegmentsAsync(session.Id);
        Assert.Equal("Meeting Guest", session.LocalSpeakerName);
        Assert.Equal("Meeting Guest", Assert.Single(segments, item => item.Source == AudioSourceKind.Microphone).SpeakerName);
        Assert.Null(Assert.Single(segments, item => item.Source == AudioSourceKind.SystemOutput).SpeakerName);
        Assert.Equal("Local Profile", settings.LocalDisplayName);
    }
    [Fact]
    public async Task ConsecutiveMeetings_OverrideDoesNotLeakIntoNextMeeting()
    {
        var capture = new FakeAudioCapture();
        await using var coordinator = new RecordingCoordinator(capture, _store, new PendingAudioQueue(), new FakeTransportFactory());
        await coordinator.StartAsync("First", Settings(), localDisplayNameOverride: "First Guest");
        capture.Emit(AudioSourceKind.Microphone, [1, 2]);
        await coordinator.StopAsync();

        await coordinator.StartAsync("Second", Settings());
        capture.Emit(AudioSourceKind.Microphone, [3, 4]);
        await coordinator.StopAsync();

        var sessions = await _store.ListSessionsAsync();
        var first = sessions.Single(item => item.Title == "First");
        var second = sessions.Single(item => item.Title == "Second");
        Assert.Equal("First Guest", Assert.Single(await _store.GetSegmentsAsync(first.Id)).SpeakerName);
        Assert.Equal("Local Profile", Assert.Single(await _store.GetSegmentsAsync(second.Id)).SpeakerName);
    }
    [Fact]
    public async Task ConsecutiveMeetings_ProviderDoesNotLeakIntoNextMeeting()
    {
        var capture = new FakeAudioCapture();
        await using var coordinator = new RecordingCoordinator(capture, _store, new PendingAudioQueue(), new FakeTransportFactory());
        await coordinator.StartAsync("First provider", Settings(), meetingProvider: MeetingProvider.GoogleMeet);
        capture.Emit(AudioSourceKind.Microphone, [1, 2]);
        await coordinator.StopAsync();
        await coordinator.StartAsync("Second provider", Settings());
        capture.Emit(AudioSourceKind.Microphone, [3, 4]);
        await coordinator.StopAsync();
        var sessions = await _store.ListSessionsAsync();
        Assert.Equal(MeetingProvider.GoogleMeet, sessions.Single(item => item.Title == "First provider").MeetingProvider);
        Assert.Equal(MeetingProvider.NotSelected, sessions.Single(item => item.Title == "Second provider").MeetingProvider);
    }
    [Fact]
    public async Task RecoverAsync_PreservesMeetingProviderFromInterruptedSession()
    {
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var interrupted = new MeetingSession(
            "recover-provider",
            "Recover provider",
            startedAt,
            startedAt.AddSeconds(10),
            SessionState.Interrupted,
            "Local Profile",
            MeetingProvider.MicrosoftTeams);
        await _store.CreateSessionAsync(interrupted);
        var pendingChunk = AudioChunk.Create(interrupted.Id, AudioSourceKind.Microphone, 0, startedAt, [1, 2]);
        await _store.SavePendingAsync(pendingChunk, DateTimeOffset.UtcNow.AddHours(1));
        var summary = Assert.Single(await _store.ListSessionsAsync());
        await using var coordinator = new RecordingCoordinator(
            new FakeAudioCapture(),
            _store,
            new PendingAudioQueue(),
            new FakeTransportFactory());
        await coordinator.RecoverAsync(summary, Settings(), [pendingChunk]);
        Assert.Equal(MeetingProvider.MicrosoftTeams, Assert.Single(await _store.ListSessionsAsync()).MeetingProvider);
    }
    private AppSettings Settings() => new("mic", null, _modelPath, "es", true, false,
        LocalDisplayName: "Local Profile", LocalProfileConfirmed: true);

    private sealed class FakeAudioCapture : IAudioCaptureService
    {
        public int StartFailuresRemaining { get; set; }
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        private EventHandler<CapturedSecond>? _secondCaptured;
        private EventHandler<string>? _captureFailed;
        public event EventHandler<CapturedSecond>? SecondCaptured { add => _secondCaptured += value; remove => _secondCaptured -= value; }
        public event EventHandler<string>? CaptureFailed { add => _captureFailed += value; remove => _captureFailed -= value; }

        public void Start(string? microphoneId, string? outputId, bool useMicrophone, bool useOutput)
        {
            StartCount++;
            if (StartFailuresRemaining-- > 0) throw new InvalidOperationException("Simulated capture startup failure.");
        }

        public void Emit(AudioSourceKind source, byte[] pcm) => _secondCaptured?.Invoke(this, new(source, pcm, 0.5f));
        public void EmitWithMetadata(AudioSourceKind source, byte[] pcm, string runId, long firstSourceSample) =>
            _secondCaptured?.Invoke(this, new(source, pcm, 0.5f, runId, firstSourceSample));
        public void EmitStamped(AudioSourceKind source, byte[] pcm, string runId, long firstSourceSample, long capturedTimestamp) =>
            _secondCaptured?.Invoke(this, new(source, pcm, 0.5f, runId, firstSourceSample, capturedTimestamp));
        public EventHandler<CapturedSecond> SnapshotHandler() => _secondCaptured ?? throw new InvalidOperationException("No capture handler is registered.");
        public EventHandler<string> SnapshotFailureHandler() => _captureFailed ?? throw new InvalidOperationException("No failure handler is registered.");
        public void Fail(string error) => _captureFailed?.Invoke(this, error);
        public Task StopAsync() { StopCount++; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeTransportFactory : ITranscriptionTransportFactory
    {
        public int StartFailuresRemaining { get; set; }
        public bool ThrowOnTranscribe { get; set; }
        public long SegmentStartMilliseconds { get; set; }
        public List<FakeTransport> Transports { get; } = [];

        public Task<ITranscriptionTransport> StartAsync(string modelPath, string language, CancellationToken cancellationToken)
        {
            if (StartFailuresRemaining-- > 0) throw new InvalidOperationException("Simulated worker startup failure.");
            var transport = new FakeTransport
            {
                ThrowOnTranscribe = ThrowOnTranscribe,
                SegmentStartMilliseconds = SegmentStartMilliseconds
            };
            Transports.Add(transport);
            return Task.FromResult<ITranscriptionTransport>(transport);
        }
    }

    private sealed class FakeTransport : ITranscriptionTransport
    {
        public string? VerifiedModelHash { get; } = "FAKE-VERIFIED-HASH";
        public int TranscriptionCount { get; private set; }
        public bool Disposed { get; private set; }
        public bool ThrowOnTranscribe { get; init; }
        public long SegmentStartMilliseconds { get; init; }
        public List<byte[]> TranscribedPcm { get; } = [];

        public Task<WorkerResponse> TranscribeAsync(string workId, byte[] pcm16, CancellationToken cancellationToken)
        {
            TranscriptionCount++;
            if (ThrowOnTranscribe) throw new InvalidOperationException("Simulated inference failure.");
            TranscribedPcm.Add([.. pcm16]);
            IReadOnlyList<WorkerSegmentDto> segments = [new(SegmentStartMilliseconds, SegmentStartMilliseconds + 900, "tail text")];
            return Task.FromResult(new WorkerResponse(true, WorkId: workId, Segments: segments));
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);

        public void AdvanceTimestamp(TimeSpan elapsed) => Interlocked.Add(ref _timestamp, elapsed.Ticks);
        public void SetUtcNow(DateTimeOffset value) => _utcNow = value;
    }
}

