namespace Trazio.AsistenteReunion.Core;

public static class AnonymousVisualEvidenceVersions
{
    public const int CurrentPayload = 1;
    public const int CurrentProfile = 1;
    public const int CurrentEvidence = 1;
    public const int CurrentDetector = 1;
    public const int CurrentPolicy = 1;
}

public enum AnonymousVisualEvidenceKind
{
    Activity = 1,
    Coverage = 2
}

public enum AnonymousVisualAnalysisAvailability
{
    Available = 1,
    Unavailable = 2
}

public sealed record AnonymousVisualEvidenceProvenance(
    MeetingProvider Provider,
    int ProfileVersion,
    int EvidenceVersion,
    int DetectorVersion,
    int PolicyVersion);

public sealed record AnonymousVisualEvidenceInterval
{
    private AnonymousVisualEvidenceInterval(
        Guid id,
        string sessionId,
        AnonymousVisualEvidenceKind kind,
        AnonymousVisualAnalysisAvailability availability,
        TimeSpan start,
        TimeSpan end,
        double confidence,
        AnonymousVisualEvidenceProvenance provenance)
    {
        if (id == Guid.Empty) throw new ArgumentException("Evidence identity must be opaque and non-empty.", nameof(id));
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (start < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(start));
        if (end <= start) throw new ArgumentOutOfRangeException(nameof(end));
        if (!double.IsFinite(confidence) || confidence is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(confidence));
        ArgumentNullException.ThrowIfNull(provenance);
        if (!Enum.IsDefined(provenance.Provider)) throw new ArgumentOutOfRangeException(nameof(provenance));
        if (provenance.ProfileVersion <= 0 || provenance.EvidenceVersion <= 0 ||
            provenance.DetectorVersion <= 0 || provenance.PolicyVersion <= 0)
            throw new ArgumentOutOfRangeException(nameof(provenance));
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (!Enum.IsDefined(availability)) throw new ArgumentOutOfRangeException(nameof(availability));
        if (kind == AnonymousVisualEvidenceKind.Activity && availability != AnonymousVisualAnalysisAvailability.Available)
            throw new ArgumentException("Positive activity can only exist inside available analysis coverage.", nameof(availability));

        Id = id;
        SessionId = sessionId;
        Kind = kind;
        Availability = availability;
        Start = start;
        End = end;
        Confidence = confidence;
        Provenance = provenance;
    }

    public Guid Id { get; }
    public string SessionId { get; }
    public AnonymousVisualEvidenceKind Kind { get; }
    public AnonymousVisualAnalysisAvailability Availability { get; }
    public TimeSpan Start { get; }
    public TimeSpan End { get; }
    public double Confidence { get; }
    public AnonymousVisualEvidenceProvenance Provenance { get; }

    public static AnonymousVisualEvidenceInterval Activity(
        Guid id,
        string sessionId,
        TimeSpan start,
        TimeSpan end,
        double confidence,
        AnonymousVisualEvidenceProvenance provenance) =>
        new(
            id,
            sessionId,
            AnonymousVisualEvidenceKind.Activity,
            AnonymousVisualAnalysisAvailability.Available,
            start,
            end,
            confidence,
            provenance);

    public static AnonymousVisualEvidenceInterval Coverage(
        Guid id,
        string sessionId,
        TimeSpan start,
        TimeSpan end,
        AnonymousVisualAnalysisAvailability availability,
        double confidence,
        AnonymousVisualEvidenceProvenance provenance) =>
        new(id, sessionId, AnonymousVisualEvidenceKind.Coverage, availability, start, end, confidence, provenance);

    internal static AnonymousVisualEvidenceInterval Restore(
        Guid id,
        string sessionId,
        AnonymousVisualEvidenceKind kind,
        AnonymousVisualAnalysisAvailability availability,
        TimeSpan start,
        TimeSpan end,
        double confidence,
        AnonymousVisualEvidenceProvenance provenance) =>
        new(id, sessionId, kind, availability, start, end, confidence, provenance);
}

public enum AnonymousVisualEvidenceReadStatus
{
    Loaded = 0,
    UnsupportedVersion = 1,
    Corrupted = 2
}

public sealed record AnonymousVisualEvidenceReadResult
{
    private AnonymousVisualEvidenceReadResult(
        string sessionId,
        AnonymousVisualEvidenceReadStatus status,
        IEnumerable<AnonymousVisualEvidenceInterval> intervals)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var copied = intervals.ToArray();
        if (copied.Any(interval => !string.Equals(interval.SessionId, sessionId, StringComparison.Ordinal)))
            throw new ArgumentException("Every interval must belong to the requested session.", nameof(intervals));

        SessionId = sessionId;
        Status = status;
        Intervals = Array.AsReadOnly(copied);
    }

    public string SessionId { get; }
    public AnonymousVisualEvidenceReadStatus Status { get; }
    public IReadOnlyList<AnonymousVisualEvidenceInterval> Intervals { get; }

    public static AnonymousVisualEvidenceReadResult Loaded(
        string sessionId,
        IEnumerable<AnonymousVisualEvidenceInterval> intervals) =>
        new(sessionId, AnonymousVisualEvidenceReadStatus.Loaded, intervals);

    public static AnonymousVisualEvidenceReadResult UnsupportedVersion(string sessionId) =>
        new(sessionId, AnonymousVisualEvidenceReadStatus.UnsupportedVersion, []);

    public static AnonymousVisualEvidenceReadResult Corrupted(string sessionId) =>
        new(sessionId, AnonymousVisualEvidenceReadStatus.Corrupted, []);
}

public enum AnonymousVisualCorrelationOutcome
{
    Matched = 0,
    InsufficientEvidence = 1,
    Unavailable = 2,
    Abstained = 3
}

public enum AnonymousVisualCorrelationReason
{
    None = 0,
    UnsupportedSource = 1,
    SessionMismatch = 2,
    InvalidSegmentRange = 3,
    UnsupportedPayloadVersion = 4,
    CorruptedEvidence = 5,
    UnsupportedEvidenceVersion = 6,
    LowConfidence = 7,
    InsufficientCoverage = 8,
    NoAvailableCoverage = 9,
    ActivityOverlapBelowMinimum = 10,
    UnsupportedProvider = 11,
    MixedProvenance = 12,
    ContradictoryAvailability = 13,
    ActivityOutsideCoverage = 14
}

public sealed record AnonymousVisualCorrelation(
    AnonymousVisualCorrelationOutcome Outcome,
    AnonymousVisualCorrelationReason Reason,
    double CoverageRatio,
    double ActivityOverlapRatio)
{
    public bool HasEvidence => Outcome == AnonymousVisualCorrelationOutcome.Matched;
}

public sealed record AnonymousVisualCorrelationPolicy(
    MeetingProvider Provider,
    int ProfileVersion,
    int EvidenceVersion,
    int DetectorVersion,
    int PolicyVersion,
    double MinimumConfidence,
    double MinimumCoverageRatio,
    double MinimumActivityOverlapRatio);

internal sealed class AnonymousVisualCorrelationEngine
{
    private readonly AnonymousVisualCorrelationPolicy _policy;

    internal AnonymousVisualCorrelationEngine(AnonymousVisualCorrelationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!Enum.IsDefined(policy.Provider)) throw new ArgumentOutOfRangeException(nameof(policy));
        if (policy.ProfileVersion <= 0 || policy.EvidenceVersion <= 0 ||
            policy.DetectorVersion <= 0 || policy.PolicyVersion <= 0)
            throw new ArgumentOutOfRangeException(nameof(policy));
        ValidateRatio(policy.MinimumConfidence, nameof(policy.MinimumConfidence));
        ValidateRatio(policy.MinimumCoverageRatio, nameof(policy.MinimumCoverageRatio));
        ValidateRatio(policy.MinimumActivityOverlapRatio, nameof(policy.MinimumActivityOverlapRatio));
        _policy = policy;
    }

    internal AnonymousVisualCorrelation Correlate(
        AnonymousVisualCorrelationSegment segment,
        string evidenceSessionId,
        AnonymousVisualEvidenceReadStatus evidenceStatus,
        IReadOnlyList<AnonymousVisualEvidenceInterval> intervals)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceSessionId);
        ArgumentNullException.ThrowIfNull(intervals);

        if (segment.AudioSourceKind != AudioSourceKind.SystemOutput)
            return Abstain(AnonymousVisualCorrelationReason.UnsupportedSource);
        if (segment.Start < TimeSpan.Zero || segment.End <= segment.Start)
            return Abstain(AnonymousVisualCorrelationReason.InvalidSegmentRange);
        if (evidenceStatus == AnonymousVisualEvidenceReadStatus.UnsupportedVersion)
            return Abstain(AnonymousVisualCorrelationReason.UnsupportedPayloadVersion);
        if (evidenceStatus == AnonymousVisualEvidenceReadStatus.Corrupted)
            return Abstain(AnonymousVisualCorrelationReason.CorruptedEvidence);
        if (!string.Equals(segment.SessionId, evidenceSessionId, StringComparison.Ordinal))
            return Abstain(AnonymousVisualCorrelationReason.SessionMismatch);

        var overlapping = intervals
            .Where(interval => interval.End > segment.Start && interval.Start < segment.End)
            .ToArray();
        if (overlapping.Length == 0)
            return Unavailable();
        var provenances = overlapping.Select(interval => interval.Provenance).Distinct().ToArray();
        if (provenances.Length > 1)
            return Abstain(AnonymousVisualCorrelationReason.MixedProvenance);
        if (provenances[0].Provider != _policy.Provider)
            return Abstain(AnonymousVisualCorrelationReason.UnsupportedProvider);
        if (!IsSupported(provenances[0]))
            return Abstain(AnonymousVisualCorrelationReason.UnsupportedEvidenceVersion);

        var durationTicks = segment.End.Ticks - segment.Start.Ticks;
        var availableCoverage = overlapping.Where(interval =>
                interval.Kind == AnonymousVisualEvidenceKind.Coverage &&
                interval.Availability == AnonymousVisualAnalysisAvailability.Available).ToArray();
        var unavailableCoverage = overlapping.Where(interval =>
                interval.Kind == AnonymousVisualEvidenceKind.Coverage &&
                interval.Availability == AnonymousVisualAnalysisAvailability.Unavailable).ToArray();
        if (HasOverlap(availableCoverage, unavailableCoverage, segment.Start, segment.End))
            return Abstain(AnonymousVisualCorrelationReason.ContradictoryAvailability);

        var activity = overlapping
            .Where(interval => interval.Kind == AnonymousVisualEvidenceKind.Activity)
            .ToArray();
        var coverageTicks = UnionOverlapTicks(
            availableCoverage,
            segment.Start,
            segment.End);
        if (coverageTicks == 0)
            return activity.Length == 0
                ? Unavailable()
                : Abstain(AnonymousVisualCorrelationReason.ActivityOutsideCoverage);
        if (availableCoverage.Concat(activity).Any(interval => interval.Confidence < _policy.MinimumConfidence))
            return Abstain(AnonymousVisualCorrelationReason.LowConfidence);

        var coverageRatio = (double)coverageTicks / durationTicks;
        var activityTicks = UnionOverlapTicks(activity, segment.Start, segment.End);
        var coveredActivityTicks = IntersectionOverlapTicks(
            activity,
            availableCoverage,
            segment.Start,
            segment.End);
        if (coveredActivityTicks != activityTicks)
            return new(
                AnonymousVisualCorrelationOutcome.Abstained,
                AnonymousVisualCorrelationReason.ActivityOutsideCoverage,
                coverageRatio,
                0);
        if (coverageRatio < _policy.MinimumCoverageRatio)
            return new(
                AnonymousVisualCorrelationOutcome.Abstained,
                AnonymousVisualCorrelationReason.InsufficientCoverage,
                coverageRatio,
                0);

        var activityRatio = (double)coveredActivityTicks / durationTicks;
        return activityRatio >= _policy.MinimumActivityOverlapRatio
            ? new(
                AnonymousVisualCorrelationOutcome.Matched,
                AnonymousVisualCorrelationReason.None,
                coverageRatio,
                activityRatio)
            : new(
                AnonymousVisualCorrelationOutcome.InsufficientEvidence,
                AnonymousVisualCorrelationReason.ActivityOverlapBelowMinimum,
                coverageRatio,
                activityRatio);
    }

    private bool IsSupported(AnonymousVisualEvidenceProvenance provenance) =>
        provenance.ProfileVersion == _policy.ProfileVersion &&
        provenance.EvidenceVersion == _policy.EvidenceVersion &&
        provenance.DetectorVersion == _policy.DetectorVersion &&
        provenance.PolicyVersion == _policy.PolicyVersion;

    private static long UnionOverlapTicks(
        IEnumerable<AnonymousVisualEvidenceInterval> intervals,
        TimeSpan segmentStart,
        TimeSpan segmentEnd) =>
        UnionTicks(intervals
            .Select(interval => (
                Start: Math.Max(interval.Start.Ticks, segmentStart.Ticks),
                End: Math.Min(interval.End.Ticks, segmentEnd.Ticks)))
            .Where(range => range.End > range.Start));

    private static long UnionTicks(IEnumerable<(long Start, long End)> ranges)
    {
        var ordered = ranges
            .OrderBy(range => range.Start)
            .ThenBy(range => range.End)
            .ToArray();
        if (ordered.Length == 0) return 0;

        var total = 0L;
        var currentStart = ordered[0].Start;
        var currentEnd = ordered[0].End;
        foreach (var range in ordered.AsSpan()[1..])
        {
            if (range.Start <= currentEnd)
            {
                currentEnd = Math.Max(currentEnd, range.End);
                continue;
            }

            total += currentEnd - currentStart;
            currentStart = range.Start;
            currentEnd = range.End;
        }

        return total + currentEnd - currentStart;
    }

    private static long IntersectionOverlapTicks(
        IEnumerable<AnonymousVisualEvidenceInterval> left,
        IEnumerable<AnonymousVisualEvidenceInterval> right,
        TimeSpan segmentStart,
        TimeSpan segmentEnd)
    {
        var intersections =
            from leftInterval in left
            from rightInterval in right
            let start = Math.Max(
                Math.Max(leftInterval.Start.Ticks, rightInterval.Start.Ticks),
                segmentStart.Ticks)
            let end = Math.Min(
                Math.Min(leftInterval.End.Ticks, rightInterval.End.Ticks),
                segmentEnd.Ticks)
            where end > start
            select (Start: start, End: end);
        return UnionTicks(intersections);
    }

    private static bool HasOverlap(
        IEnumerable<AnonymousVisualEvidenceInterval> left,
        IEnumerable<AnonymousVisualEvidenceInterval> right,
        TimeSpan segmentStart,
        TimeSpan segmentEnd) =>
        left.Any(leftInterval => right.Any(rightInterval =>
            Math.Max(Math.Max(leftInterval.Start.Ticks, rightInterval.Start.Ticks), segmentStart.Ticks) <
            Math.Min(Math.Min(leftInterval.End.Ticks, rightInterval.End.Ticks), segmentEnd.Ticks)));

    private static AnonymousVisualCorrelation Abstain(AnonymousVisualCorrelationReason reason) =>
        new(AnonymousVisualCorrelationOutcome.Abstained, reason, 0, 0);

    private static AnonymousVisualCorrelation Unavailable() =>
        new(
            AnonymousVisualCorrelationOutcome.Unavailable,
            AnonymousVisualCorrelationReason.NoAvailableCoverage,
            0,
            0);

    private static void ValidateRatio(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value is < 0 or > 1)
            throw new ArgumentOutOfRangeException(parameterName);
    }
}
