using Trazio.AsistenteReunion.App;
using Trazio.AsistenteReunion.Core;
using Windows.Graphics.DirectX.Direct3D11;

namespace Trazio.AsistenteReunion.Tests;

public sealed class VisualProbeSessionTests
{
    [Theory]
    [InlineData(MeetingProvider.GoogleMeet)]
    [InlineData(MeetingProvider.MicrosoftTeams)]
    [Trait("Area", "VisualCapture")]
    public async Task ProductionProfile_EmitsOnlyUnavailableCoverageWithoutAccessingSurface(
        MeetingProvider provider)
    {
        var context = new MutableTimelineContext();
        var sink = new RecordingSink();
        await using var session = VisualProbeSession.Start(context, provider, sink);
        Assert.True(session.BeginSurface(out var revision));
        context.Advance(TimeSpan.FromSeconds(1));
        var surfaceAccessCount = 0;

        var result = session.ObserveFrame(revision, () =>
        {
            surfaceAccessCount++;
            throw new InvalidOperationException("Production profiles must not inspect Surface.");
        });
        context.Advance(TimeSpan.FromSeconds(1));
        await session.CompleteAt(context.Offset);

        Assert.Equal(VisualProbeFrameResult.ProfileUnavailable, result);
        Assert.Equal(0, surfaceAccessCount);
        Assert.NotEmpty(sink.Intervals);
        Assert.All(sink.Intervals, interval =>
        {
            Assert.Equal(AnonymousVisualEvidenceKind.Coverage, interval.Kind);
            Assert.Equal(AnonymousVisualAnalysisAvailability.Unavailable, interval.Availability);
            Assert.Equal(0, interval.Confidence);
        });
        Assert.Equal(TimeSpan.FromSeconds(2), sink.Intervals.Last().End);
        Assert.Equal(0, session.DetectorFailureCount);
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public async Task Start_WithoutAnyFrame_StillPersistsUnavailableCoverage()
    {
        var context = new MutableTimelineContext();
        var sink = new RecordingSink();
        await using var session = VisualProbeSession.Start(
            context,
            MeetingProvider.GoogleMeet,
            sink);
        context.Advance(TimeSpan.FromSeconds(2));

        await session.CompleteAt(context.Offset);

        var coverage = Assert.Single(sink.Intervals);
        Assert.Equal(AnonymousVisualEvidenceKind.Coverage, coverage.Kind);
        Assert.Equal(AnonymousVisualAnalysisAvailability.Unavailable, coverage.Availability);
        Assert.Equal(0, coverage.Confidence);
        Assert.Equal(TimeSpan.Zero, coverage.Start);
        Assert.Equal(TimeSpan.FromSeconds(2), coverage.End);
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public async Task ValidatedProfile_AccessesSurfaceAndExtractorOnlyAfterRateGateAccepts()
    {
        var context = new MutableTimelineContext();
        var extractor = new RecordingExtractor();
        var sink = new RecordingSink();
        await using var session = VisualProbeSession.Start(
            context,
            CreateValidatedProfile(),
            extractor,
            sink);
        Assert.True(session.BeginSurface(out var revision));
        var surfaceAccessCount = 0;
        IDirect3DSurface GetSurface()
        {
            surfaceAccessCount++;
            return null!;
        }

        var rejected = session.ObserveFrame(revision, GetSurface);
        context.Advance(TimeSpan.FromSeconds(1));
        var accepted = session.ObserveFrame(revision, GetSurface);
        context.Advance(TimeSpan.FromSeconds(1));
        await session.CompleteAt(context.Offset);

        Assert.Equal(VisualProbeFrameResult.RateLimited, rejected);
        Assert.Equal(VisualProbeFrameResult.Observed, accepted);
        Assert.Equal(1, surfaceAccessCount);
        Assert.Equal(1, extractor.ExtractCount);
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public async Task ExtractorFailure_IsTypedAndContainedInsideVisualBranch()
    {
        var context = new MutableTimelineContext();
        var extractor = new RecordingExtractor
        {
            Exception = new VisualProbeExtractionException(VisualProbeExtractionFailure.DimensionMismatch)
        };
        var sink = new RecordingSink();
        await using var session = VisualProbeSession.Start(
            context,
            CreateValidatedProfile(),
            extractor,
            sink);
        Assert.True(session.BeginSurface(out var revision));
        context.Advance(TimeSpan.FromSeconds(1));

        var result = session.ObserveFrame(revision, () => null!);
        context.Advance(TimeSpan.FromSeconds(1));
        await session.CompleteAt(context.Offset);

        Assert.Equal(VisualProbeFrameResult.ExtractionFailed, result);
        Assert.Equal(1, session.ExtractionFailureCount);
        Assert.DoesNotContain(sink.Intervals, interval => interval.Kind == AnonymousVisualEvidenceKind.Activity);
        Assert.All(sink.Intervals, interval =>
            Assert.Equal(AnonymousVisualAnalysisAvailability.Unavailable, interval.Availability));
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public async Task MarkUnavailable_AtSameOffsetAsAvailable_ReplacesPendingObservationFailClosed()
    {
        var context = new MutableTimelineContext();
        var sink = new RecordingSink();
        await using var session = VisualProbeSession.Start(
            context,
            CreateValidatedProfile(),
            new RecordingExtractor(),
            sink);
        Assert.True(session.BeginSurface(out var revision));
        context.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(
            VisualProbeFrameResult.Observed,
            session.ObserveFrame(revision, () => null!));
        context.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(
            VisualProbeFrameResult.Observed,
            session.ObserveFrame(revision, () => null!));
        await WaitUntilAsync(() => sink.Intervals.Count > 0);

        Assert.True(session.MarkUnavailable(revision));
        context.Advance(TimeSpan.FromSeconds(1));
        await session.CompleteAt(context.Offset);

        var available = Assert.Single(sink.Intervals, interval =>
            interval.Kind == AnonymousVisualEvidenceKind.Coverage &&
            interval.Availability == AnonymousVisualAnalysisAvailability.Available);
        Assert.Equal(TimeSpan.FromSeconds(1), available.Start);
        Assert.Equal(TimeSpan.FromSeconds(2), available.End);
        var terminalUnavailable = Assert.Single(sink.Intervals, interval =>
            interval.Kind == AnonymousVisualEvidenceKind.Coverage &&
            interval.Availability == AnonymousVisualAnalysisAvailability.Unavailable &&
            interval.Start == TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.FromSeconds(3), terminalUnavailable.End);
        Assert.Equal(0, session.DetectorFailureCount);
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public async Task SinkFailure_IsContainedAndReportedByVisualPipeline()
    {
        var context = new MutableTimelineContext();
        var sink = new RecordingSink { ThrowOnWrite = true };
        await using var session = VisualProbeSession.Start(
            context,
            MeetingProvider.GoogleMeet,
            sink);
        context.Advance(TimeSpan.FromSeconds(1));

        await session.CompleteAt(context.Offset);

        Assert.Equal(1, session.SinkFailureCount);
        Assert.Equal(1, sink.WriteCount);
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public async Task SurfaceRevision_RemainsMonotonicAcrossRepeatedLeaseBegins()
    {
        var context = new MutableTimelineContext();
        await using var session = VisualProbeSession.Start(
            context,
            MeetingProvider.GoogleMeet,
            new RecordingSink());

        Assert.True(session.BeginSurface(out var first));
        context.Advance(TimeSpan.FromMilliseconds(100));
        Assert.True(session.BeginSurface(out var second));
        context.Advance(TimeSpan.FromMilliseconds(100));
        Assert.True(session.BeginSurface(out var third));
        context.Advance(TimeSpan.FromSeconds(1));
        await session.CompleteAt(context.Offset);

        Assert.Equal(1, first);
        Assert.Equal(2, second);
        Assert.Equal(3, third);
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public async Task RevokedContext_RejectsFrameBeforeSurfaceAccess()
    {
        var context = new MutableTimelineContext();
        await using var session = VisualProbeSession.Start(
            context,
            CreateValidatedProfile(),
            new RecordingExtractor(),
            new RecordingSink());
        Assert.True(session.BeginSurface(out var revision));
        context.Revoke();
        var surfaceAccessCount = 0;

        var result = session.ObserveFrame(revision, () =>
        {
            surfaceAccessCount++;
            return null!;
        });

        Assert.Equal(VisualProbeFrameResult.ContextUnavailable, result);
        Assert.Equal(0, surfaceAccessCount);
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public async Task CompleteAndDispose_RaceIsIdempotentAndRejectsLateObservations()
    {
        var context = new MutableTimelineContext();
        var session = VisualProbeSession.Start(
            context,
            MeetingProvider.GoogleMeet,
            new RecordingSink());
        Assert.True(session.BeginSurface(out var revision));
        context.Advance(TimeSpan.FromSeconds(2));

        var completions = Enumerable.Range(0, 16)
            .Select(index => index % 2 == 0
                ? session.CompleteAt(context.Offset).AsTask()
                : session.DisposeAsync().AsTask())
            .ToArray();
        await Task.WhenAll(completions);
        var surfaceAccessCount = 0;
        var lateResult = session.ObserveFrame(revision, () =>
        {
            surfaceAccessCount++;
            return null!;
        });

        Assert.Equal(VisualProbeFrameResult.PipelineUnavailable, lateResult);
        Assert.False(session.BeginSurface(out _));
        Assert.False(session.MarkUnavailable(revision));
        Assert.Equal(0, surfaceAccessCount);
        await session.DisposeAsync();
    }

    private static VisualProbeLayoutProfile CreateValidatedProfile() =>
        new(
            MeetingProvider.GoogleMeet,
            version: 99,
            VisualProbeProfileValidationState.Validated,
            expectedSurfaceWidth: 16,
            expectedSurfaceHeight: 16,
            [new VisualProbePatch(0, 0, 1, 2, 3, 4)],
            new VisualProbeDetectionPolicy(
                enterThreshold: 700,
                exitThreshold: 300,
                activationHold: TimeSpan.FromSeconds(1),
                releaseHold: TimeSpan.FromSeconds(1),
                maxObservationGap: TimeSpan.FromSeconds(2),
                minimumCoherentPatches: 1,
                evidenceVersion: 1,
                detectorVersion: 1,
                policyVersion: 1));

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!condition())
        {
            if (DateTime.UtcNow >= timeout)
                throw new TimeoutException("The visual probe pipeline did not make progress.");
            await Task.Delay(10);
        }
    }

    private sealed class MutableTimelineContext : ISessionTimelineContext
    {
        private int _active = 1;

        public string SessionId { get; } = Guid.NewGuid().ToString("N");
        public long Revision => 1;
        public DateTimeOffset StartedAtUtc { get; } = DateTimeOffset.UtcNow;
        public TimeSpan Offset { get; private set; }

        public bool TryGetCurrentOffset(out TimeSpan offset)
        {
            offset = Offset;
            return Volatile.Read(ref _active) != 0;
        }

        public void Advance(TimeSpan elapsed) => Offset += elapsed;
        public void Revoke() => Interlocked.Exchange(ref _active, 0);
    }

    private sealed class RecordingExtractor : IVisualProbeExtractor
    {
        public Exception? Exception { get; init; }
        public int ExtractCount { get; private set; }

        public VisualProbeFeatureLease Extract(
            IDirect3DSurface surface,
            VisualProbeLayoutProfile profile,
            TimeSpan offset,
            long surfaceRevision)
        {
            ExtractCount++;
            if (Exception is not null) throw Exception;
            return VisualProbeFeatureLease.Create(
                VisualProbeObservation.Available(offset, surfaceRevision, profile),
                [new VisualProbeFeature(0)]);
        }
    }

    private sealed class RecordingSink : IVisualProbeEvidenceSink
    {
        private readonly object _gate = new();
        private readonly List<AnonymousVisualEvidenceInterval> _intervals = [];

        public bool ThrowOnWrite { get; init; }
        public int WriteCount { get; private set; }
        public IReadOnlyList<AnonymousVisualEvidenceInterval> Intervals
        {
            get
            {
                lock (_gate) return [.. _intervals];
            }
        }

        public ValueTask WriteAsync(
            AnonymousVisualEvidenceInterval interval,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                WriteCount++;
                if (!ThrowOnWrite) _intervals.Add(interval);
            }

            return ThrowOnWrite
                ? ValueTask.FromException(new InvalidOperationException("sink failure"))
                : ValueTask.CompletedTask;
        }
    }
}
