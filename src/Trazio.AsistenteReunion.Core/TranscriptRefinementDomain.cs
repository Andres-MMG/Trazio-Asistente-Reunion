using System.Text;

namespace Trazio.AsistenteReunion.Core;

public enum RefinementReviewStatus { Unreviewed, Accepted, Rejected }

public sealed record TranscriptRefinementDraft(string Text, bool IsAmbiguousAlternative = false);

public sealed record TranscriptRefinementProposal(
    string Id,
    string BatchId,
    int Ordinal,
    string Text,
    bool IsAmbiguousAlternative,
    RefinementReviewStatus ReviewStatus,
    string? ReviewerName,
    DateTimeOffset? ReviewedAt,
    string? SourceCorrectionId);

public sealed record TranscriptRefinementBatch(
    string Id,
    string SegmentId,
    string SessionId,
    AudioSourceKind Source,
    TimeSpan Start,
    TimeSpan End,
    string OriginalText,
    string GeneratorIdentity,
    string ConfigurationFingerprint,
    DateTimeOffset CreatedAt,
    IReadOnlyList<TranscriptRefinementProposal> Proposals);

public static class TranscriptRefinementPolicy
{
    public const int MaxTextCharacters = 4_000;
    public const int MaxModelIdentityCharacters = 200;
    public const int MaxReviewerCharacters = 120;

    public static IReadOnlyList<TranscriptRefinementDraft> ValidateDrafts(
        string originalText,
        IReadOnlyList<TranscriptRefinementDraft> drafts)
    {
        ArgumentNullException.ThrowIfNull(drafts);
        ValidateText(originalText, nameof(originalText));
        if (drafts.Count > 2)
            throw new ArgumentException("Solo se permiten dos propuestas por análisis.", nameof(drafts));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        seen.Add(NormalizeForComparison(originalText));
        var result = new List<TranscriptRefinementDraft>(drafts.Count);
        for (var index = 0; index < drafts.Count; index++)
        {
            var draft = drafts[index] ?? throw new ArgumentException("La propuesta no puede estar vacía.", nameof(drafts));
            ValidateText(draft.Text, nameof(drafts));
            if (draft.IsAmbiguousAlternative != (index == 1))
                throw new ArgumentException("Solo la segunda propuesta puede marcarse como alternativa ambigua.", nameof(drafts));
            var text = draft.Text.Trim();
            if (!seen.Add(NormalizeForComparison(text)))
                throw new ArgumentException("La propuesta duplica el original u otra propuesta.", nameof(drafts));
            result.Add(draft with { Text = text });
        }
        return result;
    }

    public static void ValidateText(string text, string parameter)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxTextCharacters)
            throw new ArgumentException("El texto debe tener entre 1 y 4000 caracteres.", parameter);
    }

    private static string NormalizeForComparison(string text) =>
        string.Join(' ', text.Normalize(NormalizationForm.FormC).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
}
