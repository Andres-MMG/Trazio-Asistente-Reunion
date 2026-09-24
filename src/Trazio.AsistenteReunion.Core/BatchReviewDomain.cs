namespace Trazio.AsistenteReunion.Core;

public enum SegmentReviewDecisionAction
{
    ApproveOriginal,
    Reopen
}

public enum SegmentReviewWriteStatus
{
    Applied,
    AlreadyCurrent,
    StateChanged,
    Missing
}

public sealed record SegmentReviewDecision(
    string Id,
    string SegmentId,
    string SessionId,
    int Revision,
    SegmentReviewDecisionAction Action,
    string ReviewerName,
    DateTimeOffset CreatedAt);

public sealed record SegmentReviewWriteResult(
    SegmentReviewWriteStatus Status,
    int CurrentRevision,
    SegmentReviewDecision? Decision = null);

public enum BatchSegmentReviewWriteStatus
{
    Applied,
    Conflict
}

public sealed record SegmentReviewApprovalRequest(
    string SessionId,
    string SegmentId,
    int ExpectedDecisionRevision);

public sealed record BatchSegmentReviewConflict(
    SegmentReviewApprovalRequest Request,
    SegmentReviewWriteStatus Status,
    int CurrentRevision);

public sealed record BatchSegmentReviewWriteResult(
    BatchSegmentReviewWriteStatus Status,
    IReadOnlyList<SegmentReviewDecision> Decisions,
    IReadOnlyList<BatchSegmentReviewConflict> Conflicts);
public sealed record PendingSegmentReview(
    string SessionId,
    string SegmentId,
    string SessionTitle,
    DateTimeOffset SessionStartedAt,
    AudioSourceKind Source,
    long Sequence,
    TimeSpan Start,
    TimeSpan End,
    string Text,
    string? SpeakerName,
    int ExpectedDecisionRevision);

public sealed record PendingSegmentReviewResult(
    IReadOnlyList<PendingSegmentReview> Items,
    int TotalCount,
    bool IsTruncated);

public static class PendingSegmentReviewLimits
{
    public const int MaximumVisibleItems = 100;
}
