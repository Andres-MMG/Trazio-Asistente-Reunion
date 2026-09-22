namespace Trazio.AsistenteReunion.Core;

public enum ModelRevisionStatus { Running, Succeeded, Failed, Cancelled }

public sealed record TranscriptModelRevision(
    string Id,
    string SessionId,
    AudioSourceKind Source,
    ModelRevisionStatus Status,
    string ModelIdentity,
    string? ModelHash,
    string Language,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    TimeSpan? ProcessingDuration,
    string? Error,
    string GlossaryPromptVersion);

public sealed record TranscriptModelRevisionSegment(
    string Id,
    string RevisionId,
    long Sequence,
    TimeSpan Start,
    TimeSpan End,
    string Text);
