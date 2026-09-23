using System.Buffers;
using Trazio.AsistenteReunion.App;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.Tests;

public sealed class VisualProbePipelineTests
{
    [Fact]
    [Trait("Area", "VisualCapture")]
    public async Task SlowConsumer_WhenCapacityIsExceeded_DropsOldestAndDisposesEveryLeaseExactlyOnce()
    {
        var detector = new RecordingDetector();
        var sink = new BlockingSink();
        var pool = new TrackingFeaturePool();
        await using var pipeline = new VisualProbePipeline(detector, sink);
        var leases = Enumerable.Range(1, 4)
            .Select(index => CreateLease(index, pool))
            .ToArray();
        Assert.True(pipeline.TryWrite(leases[0]));
        var run = pipeline.RunAsync();
        await sink.Started.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(pipeline.TryWrite(leases[1]));
        Assert.True(pipeline.TryWrite(leases[2]));
        Assert.True(pipeline.TryWrite(leases[3]));
        Assert.Equal(1, pipeline.DroppedCount);

        sink.Release();
        await WaitUntilAsync(() => detector.SurfaceRevisions.Count == 3);
        Assert.True(pipeline.CompleteAt(TimeSpan.FromSeconds(5)));
        await run;

        Assert.Equal([1L, 3L, 4L], detector.SurfaceRevisions);
        Assert.Equal(4, pool.ReturnCounts.Count);
        Assert.All(pool.ReturnCounts.Values, count => Assert.Equal(1, count));
        Assert.All(pool.ReturnedSnapshots, snapshot =>
            Assert.All(snapshot, feature => Assert.Equal(default, feature)));
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public async Task RunAsync_WhenDetectorThrows_ContainsFailureInsideVisualPipeline()
    {
        var detector = new RecordingDetector { ThrowOnObservation = true };
        var sink = new RecordingSink();
        var pool = new TrackingFeaturePool();
        await using var pipeline = new VisualProbePipeline(detector, sink);
        Assert.True(pipeline.TryWrite(CreateLease(1, pool)));
        Assert.True(pipeline.CompleteAt(TimeSpan.FromSeconds(2)));

        await pipeline.RunAsync();

        Assert.Equal(1, pipeline.DetectorFailureCount);
        Assert.Empty(sink.Intervals);
        Assert.Single(pool.ReturnCounts);
        Assert.Equal(1, pool.ReturnCounts.Values.Single());
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public async Task RunAsync_WhenAsyncSinkThrows_ContinuesAndDoesNotPropagateOutsideVisualPipeline()
    {
        var detector = new RecordingDetector();
        var sink = new RecordingSink { ThrowOnWrite = true };
        var pool = new TrackingFeaturePool();
        await using var pipeline = new VisualProbePipeline(detector, sink);
        Assert.True(pipeline.TryWrite(CreateLease(1, pool)));
        Assert.True(pipeline.TryWrite(CreateLease(2, pool)));
        var run = pipeline.RunAsync();
        await WaitUntilAsync(() => detector.SurfaceRevisions.Count == 2);
        Assert.True(pipeline.CompleteAt(TimeSpan.FromSeconds(3)));

        await run;

        Assert.Equal(0, pipeline.DetectorFailureCount);
        Assert.Equal(2, pipeline.SinkFailureCount);
        Assert.Equal(2, sink.WriteCount);
        Assert.All(pool.ReturnCounts.Values, count => Assert.Equal(1, count));
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public async Task CompleteAt_RacingWithProducer_NeverAcceptsAnObservationAfterCompletion()
    {
        for (var iteration = 0; iteration < 32; iteration++)
        {
            var detector = new RecordingDetector();
            var sink = new RecordingSink();
            var pool = new TrackingFeaturePool();
            await using var pipeline = new VisualProbePipeline(detector, sink);
            var lease = CreateLease(1, pool);
            using var start = new Barrier(2);

            var write = Task.Run(() =>
            {
                start.SignalAndWait();
                return pipeline.TryWrite(lease);
            });
            var complete = Task.Run(() =>
            {
                start.SignalAndWait();
                return pipeline.CompleteAt(TimeSpan.FromSeconds(2));
            });

            await Task.WhenAll(write, complete);
            var writeAccepted = await write;
            var completionAccepted = await complete;
            await pipeline.RunAsync();

            Assert.True(completionAccepted);
            Assert.Equal(0, pipeline.DetectorFailureCount);
            Assert.Single(pool.ReturnCounts);
            Assert.Equal(1, pool.ReturnCounts.Values.Single());
            if (writeAccepted) Assert.Equal([1L], detector.SurfaceRevisions);
            else Assert.Empty(detector.SurfaceRevisions);
        }
    }

    private static VisualProbeFeatureLease CreateLease(int index, ArrayPool<VisualProbeFeature> pool) =>
        VisualProbeFeatureLease.Create(
            VisualProbeObservation.Available(
                TimeSpan.FromSeconds(index - 1),
                index,
                SyntheticProfile.Instance),
            [new VisualProbeFeature(900)],
            pool);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!condition())
        {
            if (DateTime.UtcNow >= timeout) throw new TimeoutException("The visual pipeline did not make progress.");
            await Task.Delay(10);
        }
    }

    private sealed class RecordingDetector : IVisualProbeDetector
    {
        private readonly object _gate = new();
        private readonly List<long> _surfaceRevisions = [];

        public bool ThrowOnObservation { get; init; }
        public IReadOnlyList<long> SurfaceRevisions
        {
            get
            {
                lock (_gate) return [.. _surfaceRevisions];
            }
        }

        public VisualProbeDetectionResult Observe(
            VisualProbeObservation observation,
            ReadOnlySpan<VisualProbeFeature> features)
        {
            if (observation.Status == VisualProbeObservationStatus.Completed)
                return VisualProbeDetectionResult.Completed([]);
            if (ThrowOnObservation) throw new InvalidOperationException("isolated detector failure");
            lock (_gate) _surfaceRevisions.Add(observation.SurfaceRevision);
            return VisualProbeDetectionResult.Observed([CreateInterval(observation.Offset)]);
        }

        private static AnonymousVisualEvidenceInterval CreateInterval(TimeSpan start) =>
            AnonymousVisualEvidenceInterval.Coverage(
                Guid.NewGuid(),
                "pipeline-test-session",
                start,
                start + TimeSpan.FromMilliseconds(1),
                AnonymousVisualAnalysisAvailability.Available,
                1,
                new(
                    MeetingProvider.GoogleMeet,
                    7,
                    AnonymousVisualEvidenceVersions.CurrentEvidence,
                    AnonymousVisualEvidenceVersions.CurrentDetector,
                    AnonymousVisualEvidenceVersions.CurrentPolicy));
    }

    private sealed class BlockingSink : IVisualProbeEvidenceSink
    {
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public void Release() => _release.TrySetResult();

        public async ValueTask WriteAsync(
            AnonymousVisualEvidenceInterval interval,
            CancellationToken cancellationToken)
        {
            _started.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class RecordingSink : IVisualProbeEvidenceSink
    {
        public bool ThrowOnWrite { get; init; }
        public int WriteCount { get; private set; }
        public List<AnonymousVisualEvidenceInterval> Intervals { get; } = [];

        public ValueTask WriteAsync(
            AnonymousVisualEvidenceInterval interval,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteCount++;
            if (ThrowOnWrite)
                return ValueTask.FromException(new InvalidOperationException("isolated sink failure"));
            Intervals.Add(interval);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TrackingFeaturePool : ArrayPool<VisualProbeFeature>
    {
        private readonly object _gate = new();

        public Dictionary<VisualProbeFeature[], int> ReturnCounts { get; } =
            new(ReferenceEqualityComparer.Instance);
        public List<VisualProbeFeature[]> ReturnedSnapshots { get; } = [];

        public override VisualProbeFeature[] Rent(int minimumLength) =>
            new VisualProbeFeature[Math.Max(1, minimumLength)];

        public override void Return(VisualProbeFeature[] array, bool clearArray = false)
        {
            lock (_gate)
            {
                ReturnCounts[array] = ReturnCounts.GetValueOrDefault(array) + 1;
                ReturnedSnapshots.Add([.. array]);
            }
        }
    }

    private sealed record SyntheticProfile : IVisualProbeProfile
    {
        public static SyntheticProfile Instance { get; } = new();
        public MeetingProvider Provider => MeetingProvider.GoogleMeet;
        public int Version => 7;
        public VisualProbeProfileValidationState ValidationState => VisualProbeProfileValidationState.Validated;
        public int ExpectedFeatureCount => 1;
        public VisualProbeDetectionPolicy DetectionPolicy { get; } = new(
            enterThreshold: 800,
            exitThreshold: 200,
            activationHold: TimeSpan.Zero,
            releaseHold: TimeSpan.Zero,
            maxObservationGap: TimeSpan.FromSeconds(2),
            minimumCoherentPatches: 1,
            evidenceVersion: 1,
            detectorVersion: 1,
            policyVersion: 1);
        VisualProbeDetectionPolicy? IVisualProbeProfile.DetectionPolicy => DetectionPolicy;
    }
}
