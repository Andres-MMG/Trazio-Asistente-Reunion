namespace Trazio.AsistenteReunion.Core;

public enum ModelRevisionStatus { Running, Succeeded, Failed, Cancelled }

public static class ModelRevisionProducer
{
    public const string Qwen3AsrLlamaCppV1 = "qwen3-asr/llama.cpp/v1";
}

public sealed record TranscriptModelRevisionScope(string SegmentId, TimeSpan Start, TimeSpan End);

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
    string GlossaryPromptVersion,
    TranscriptModelRevisionScope? Scope = null,
    string? ProducerIdentity = null);

public sealed record TranscriptModelRevisionSegment(
    string Id,
    string RevisionId,
    long Sequence,
    TimeSpan Start,
    TimeSpan End,
    string Text);
