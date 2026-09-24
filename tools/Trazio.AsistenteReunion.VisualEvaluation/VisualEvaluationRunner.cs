using Trazio.AsistenteReunion.App;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.VisualEvaluation;

public sealed class VisualEvaluationRunner
{
    public VisualEvaluationReport Evaluate(VisualEvaluationCorpus corpus)
    {
        ArgumentNullException.ThrowIfNull(corpus);
        VisualEvaluationCorpusValidator.Validate(corpus);

        var suites = corpus.Suites
            .OrderBy(suite => suite.Id, StringComparer.Ordinal)
            .Select(EvaluateSuite)
            .ToArray();
        return new(
            VisualEvaluationReportVersions.CurrentSchema,
            corpus.CorpusId,
            corpus.Scope,
            Array.AsReadOnly(suites));
    }

    private static VisualEvaluationSuiteReport EvaluateSuite(VisualEvaluationSuite suite)
    {
        var provider = ParseProvider(suite.Provider);
        var candidates = suite.Candidates
            .OrderBy(candidate => candidate.Id, StringComparer.Ordinal)
            .Select(candidate => EvaluateCandidate(suite, candidate, provider))
            .ToArray();
        return new(
            suite.Id,
            suite.Provider,
            suite.ProfileVersion,
            suite.EvidenceVersion,
            suite.DetectorVersion,
            suite.ExpectedFeatureCount,
            Array.AsReadOnly(candidates));
    }

    private static VisualEvaluationCandidateReport EvaluateCandidate(
        VisualEvaluationSuite suite,
        VisualEvaluationCandidate candidate,
        MeetingProvider provider)
    {
        var evaluations = suite.Scenarios
            .OrderBy(scenario => scenario.Id, StringComparer.Ordinal)
            .Select(scenario => EvaluateScenario(suite, candidate, scenario, provider))
            .ToArray();
        var metrics = evaluations.Aggregate(
            EmptyCounts,
            (total, evaluation) => total + evaluation.Report.Metrics);
        var denominators = evaluations.Aggregate(
            EmptyDenominators,
            (total, evaluation) => total + evaluation.Report.Denominators);
        var latencySamples = evaluations.SelectMany(evaluation => evaluation.LatencySamples).ToArray();
        var scenarioReports = evaluations.Select(evaluation => evaluation.Report).ToArray();

        return new(
            candidate.Id,
            candidate.PolicyVersion,
            metrics,
            denominators,
            VisualEvaluationRatios.Create(metrics, denominators),
            VisualEvaluationLatency.Create(latencySamples),
            VisualEvaluationResources.Aggregate(scenarioReports.Select(report => report.Resources)),
            Array.AsReadOnly(scenarioReports));
    }

    private static ScenarioEvaluation EvaluateScenario(
        VisualEvaluationSuite suite,
        VisualEvaluationCandidate candidate,
        VisualEvaluationScenario scenario,
        MeetingProvider provider)
    {
        var profile = CreateProfile(suite, candidate, provider);
        var sessionId = $"ve-{suite.Id}-{candidate.Id}-{scenario.Id}";
        var detector = new DeterministicVisualActivityDetector(sessionId);
        var correlator = CreateCorrelator(suite, candidate, provider);
        var workBudget = new ScenarioWorkBudget();
        var intervals = new List<AnonymousVisualEvidenceInterval>();
        var latencyTargets = scenario.Segments
            .Where(segment => segment.Truth == VisualEvaluationTruth.RemoteActivity)
            .Select(segment => new LatencyTarget(
                CreateSegment(segment, sessionId),
                segment.StartMs,
                segment.EndMs))
            .ToArray();
        var latencySamples = new List<long>();
        var detectorCalls = 0L;
        var correlationCalls = 0L;
        var featureCells = 0L;
        var maxFeatures = 0;
        var peakRetainedIntervals = 0L;
        var evidenceRevision = 0L;

        foreach (var observation in scenario.Observations)
        {
            workBudget.ChargeDetectorFeatureWork(observation.Features.Count);
            var features = observation.Features.Select(ToProbeFeature).ToArray();
            featureCells += checked(features.Length * 3L);
            maxFeatures = Math.Max(maxFeatures, features.Length);
            var probeObservation = observation.Status == VisualEvaluationObservationStatus.Available
                ? VisualProbeObservation.Available(
                    ToTimeSpan(observation.AtMs),
                    observation.SurfaceRevision,
                    profile)
                : VisualProbeObservation.Unavailable(
                    ToTimeSpan(observation.AtMs),
                    observation.SurfaceRevision,
                    profile);

            var result = detector.Observe(probeObservation, features);
            detectorCalls++;
            AppendIntervals(intervals, result.Intervals);
            if (result.Intervals.Count > 0) evidenceRevision++;
            peakRetainedIntervals = Math.Max(peakRetainedIntervals, intervals.Count);
            MeasureLatenciesAtCheckpoint(
                observation.AtMs,
                sessionId,
                intervals,
                evidenceRevision,
                latencyTargets,
                latencySamples,
                correlator,
                workBudget,
                ref correlationCalls);
        }

        workBudget.ChargeDetectorFeatureWork(0);
        var completed = detector.Observe(
            VisualProbeObservation.Completed(ToTimeSpan(scenario.CompletionAtMs)),
            []);
        detectorCalls++;
        AppendIntervals(intervals, completed.Intervals);
        if (completed.Intervals.Count > 0) evidenceRevision++;
        peakRetainedIntervals = Math.Max(peakRetainedIntervals, intervals.Count);
        MeasureLatenciesAtCheckpoint(
            scenario.CompletionAtMs,
            sessionId,
            intervals,
            evidenceRevision,
            latencyTargets,
            latencySamples,
            correlator,
            workBudget,
            ref correlationCalls);
        var metrics = EmptyCounts;
        var denominators = EmptyDenominators;

        for (var index = 0; index < scenario.Segments.Count; index++)
        {
            var segment = scenario.Segments[index];
            var correlationSegment = CreateSegment(segment, sessionId);
            workBudget.ChargeCorrelationWork(intervals.Count);
            var correlation = correlator.Correlate(
                correlationSegment,
                sessionId,
                AnonymousVisualEvidenceReadStatus.Loaded,
                intervals);
            correlationCalls++;
            metrics += Classify(segment.Truth, correlation);
            denominators += DenominatorFor(segment.Truth);
        }

        var resources = new VisualEvaluationResources(
            detectorCalls,
            scenario.Observations.Count,
            featureCells,
            correlationCalls,
            intervals.Count,
            peakRetainedIntervals,
            maxFeatures);
        var report = new VisualEvaluationScenarioReport(
            scenario.Id,
            metrics,
            denominators,
            VisualEvaluationRatios.Create(metrics, denominators),
            VisualEvaluationLatency.Create(latencySamples),
            resources);
        return new(report, latencySamples.ToArray());
    }

    private static void MeasureLatenciesAtCheckpoint(
        long checkpointAtMs,
        string sessionId,
        IReadOnlyList<AnonymousVisualEvidenceInterval> intervals,
        long evidenceRevision,
        IReadOnlyList<LatencyTarget> targets,
        List<long> samples,
        AnonymousVisualCorrelationEngine correlator,
        ScenarioWorkBudget workBudget,
        ref long correlationCalls)
    {
        foreach (var target in targets)
        {
            if (target.IsMatched || checkpointAtMs < target.EndMs) continue;
            if (target.LastEvaluatedEvidenceRevision == evidenceRevision) continue;

            workBudget.ChargeCorrelationWork(intervals.Count);
            var correlation = correlator.Correlate(
                target.Segment,
                sessionId,
                AnonymousVisualEvidenceReadStatus.Loaded,
                intervals);
            correlationCalls++;
            target.LastEvaluatedEvidenceRevision = evidenceRevision;
            if (correlation.Outcome != AnonymousVisualCorrelationOutcome.Matched) continue;
            target.IsMatched = true;
            samples.Add(checked(checkpointAtMs - target.StartMs));
        }
    }

    private static void AppendIntervals(
        List<AnonymousVisualEvidenceInterval> retained,
        IReadOnlyList<AnonymousVisualEvidenceInterval> emitted)
    {
        if (retained.Count + emitted.Count > VisualEvaluationLimits.MaximumGeneratedIntervalsPerScenario)
            throw new VisualEvaluationCorpusException("Generated evidence exceeds the retained interval limit.");
        retained.AddRange(emitted);
    }

    private static SyntheticVisualProbeProfile CreateProfile(
        VisualEvaluationSuite suite,
        VisualEvaluationCandidate candidate,
        MeetingProvider provider) => new(
            provider,
            suite.ProfileVersion,
            suite.ExpectedFeatureCount,
            new VisualProbeDetectionPolicy(
                candidate.Detector.EnterScore,
                candidate.Detector.ExitScore,
                ToTimeSpan(candidate.Detector.ActivationHoldMs),
                ToTimeSpan(candidate.Detector.ReleaseHoldMs),
                ToTimeSpan(candidate.Detector.MaxObservationGapMs),
                candidate.Detector.MinimumCoherentFeatures,
                suite.EvidenceVersion,
                suite.DetectorVersion,
                candidate.PolicyVersion));

    private static AnonymousVisualCorrelationEngine CreateCorrelator(
        VisualEvaluationSuite suite,
        VisualEvaluationCandidate candidate,
        MeetingProvider provider) => new(new AnonymousVisualCorrelationPolicy(
            provider,
            suite.ProfileVersion,
            suite.EvidenceVersion,
            suite.DetectorVersion,
            candidate.PolicyVersion,
            ToRatio(candidate.Correlation.MinimumConfidencePpm),
            ToRatio(candidate.Correlation.MinimumCoveragePpm),
            ToRatio(candidate.Correlation.MinimumActivityOverlapPpm)));

    private static VisualProbeFeature ToProbeFeature(VisualEvaluationFeature feature) => new(
        ToRatio(feature.HighlightMatchPpm),
        ToRatio(feature.NonBlackPpm),
        ToRatio(feature.MeanLumaPpm));

    private static AnonymousVisualCorrelationSegment CreateSegment(
        VisualEvaluationSegment segment,
        string sessionId) => new(
            sessionId,
            segment.Source == VisualEvaluationAudioSource.SystemOutput
                ? AudioSourceKind.SystemOutput
                : AudioSourceKind.Microphone,
            ToTimeSpan(segment.StartMs),
            ToTimeSpan(segment.EndMs));

    private static VisualEvaluationCounts Classify(
        VisualEvaluationTruth truth,
        AnonymousVisualCorrelation correlation)
    {
        if (truth == VisualEvaluationTruth.MicrophoneExcluded)
        {
            if (correlation.Outcome != AnonymousVisualCorrelationOutcome.Abstained ||
                correlation.Reason != AnonymousVisualCorrelationReason.UnsupportedSource)
                throw new InvalidOperationException("Microphone segments must be excluded by the production correlator.");
            return new(0, 0, 0, 0, 0, 0, 1);
        }

        return correlation.Outcome switch
        {
            AnonymousVisualCorrelationOutcome.Matched when truth == VisualEvaluationTruth.RemoteActivity =>
                new(1, 0, 0, 0, 0, 0, 0),
            AnonymousVisualCorrelationOutcome.Matched =>
                new(0, 0, 1, 0, 0, 0, 0),
            AnonymousVisualCorrelationOutcome.InsufficientEvidence when truth == VisualEvaluationTruth.RemoteActivity =>
                new(0, 1, 0, 0, 0, 0, 0),
            AnonymousVisualCorrelationOutcome.InsufficientEvidence =>
                new(0, 0, 0, 0, 0, 1, 0),
            AnonymousVisualCorrelationOutcome.Unavailable =>
                new(0, 0, 0, 0, 1, 0, 0),
            _ => new(0, 0, 0, 1, 0, 0, 0)
        };
    }

    private static VisualEvaluationDenominators DenominatorFor(VisualEvaluationTruth truth) => truth switch
    {
        VisualEvaluationTruth.RemoteActivity => new(1, 1, 1, 0, 0),
        VisualEvaluationTruth.RemoteInactive => new(1, 1, 0, 1, 0),
        VisualEvaluationTruth.RemoteUnavailable => new(1, 1, 0, 0, 1),
        VisualEvaluationTruth.MicrophoneExcluded => new(1, 0, 0, 0, 0),
        _ => throw new ArgumentOutOfRangeException(nameof(truth))
    };

    private static MeetingProvider ParseProvider(string provider) => provider switch
    {
        "google-meet" => MeetingProvider.GoogleMeet,
        "microsoft-teams" => MeetingProvider.MicrosoftTeams,
        _ => throw new ArgumentOutOfRangeException(nameof(provider))
    };

    private static double ToRatio(int ppm) => ppm / 1_000_000d;

    private static TimeSpan ToTimeSpan(long milliseconds) =>
        TimeSpan.FromTicks(checked(milliseconds * TimeSpan.TicksPerMillisecond));

    private static readonly VisualEvaluationCounts EmptyCounts = new(0, 0, 0, 0, 0, 0, 0);
    private static readonly VisualEvaluationDenominators EmptyDenominators = new(0, 0, 0, 0, 0);

    private sealed record SyntheticVisualProbeProfile(
        MeetingProvider Provider,
        int Version,
        int ExpectedFeatureCount,
        VisualProbeDetectionPolicy DetectionPolicy) : IVisualProbeProfile
    {
        public VisualProbeProfileValidationState ValidationState => VisualProbeProfileValidationState.Validated;
        VisualProbeDetectionPolicy? IVisualProbeProfile.DetectionPolicy => DetectionPolicy;
    }

    private sealed class LatencyTarget(
        AnonymousVisualCorrelationSegment segment,
        long startMs,
        long endMs)
    {
        public AnonymousVisualCorrelationSegment Segment { get; } = segment;
        public long StartMs { get; } = startMs;
        public long EndMs { get; } = endMs;
        public bool IsMatched { get; set; }
        public long LastEvaluatedEvidenceRevision { get; set; } = -1;
    }

    private sealed class ScenarioWorkBudget
    {
        private long _detectorFeatureWork;
        private long _correlationWork;

        public void ChargeDetectorFeatureWork(int featureCount)
        {
            if (featureCount < 0 ||
                featureCount > VisualEvaluationLimits.MaximumDetectorFeatureWorkPerScenario - _detectorFeatureWork)
                RejectWorkBudget();
            _detectorFeatureWork += featureCount;
        }

        public void ChargeCorrelationWork(int retainedIntervalCount)
        {
            var work = EstimateCorrelationWork(retainedIntervalCount);
            if (work > VisualEvaluationLimits.MaximumCorrelationWorkPerScenario - _correlationWork)
                RejectWorkBudget();
            _correlationWork += work;
        }

        private static long EstimateCorrelationWork(int retainedIntervalCount)
        {
            if (retainedIntervalCount < 0) RejectWorkBudget();
            var count = (long)retainedIntervalCount;
            var sortComparisons = count <= 1
                ? 0
                : checked(count * (long)Math.Ceiling(Math.Log2(count)));
            return checked(1 + checked(count * count) + sortComparisons);
        }

        private static void RejectWorkBudget() =>
            throw new VisualEvaluationCorpusException("The corpus exceeds the deterministic evaluation work budget.");
    }

    private sealed record ScenarioEvaluation(
        VisualEvaluationScenarioReport Report,
        IReadOnlyList<long> LatencySamples);
}
