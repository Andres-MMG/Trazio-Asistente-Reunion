namespace Trazio.AsistenteReunion.Core;

public sealed class AnonymousVisualActivityCorrelator
{
    private readonly AnonymousVisualCorrelationEngine _engine;

    public AnonymousVisualActivityCorrelator(AnonymousVisualCorrelationPolicy policy)
    {
        _engine = new AnonymousVisualCorrelationEngine(policy);
    }

    public AnonymousVisualCorrelation Correlate(
        TranscriptSegment segment,
        AnonymousVisualEvidenceReadResult evidence)
    {
        ArgumentNullException.ThrowIfNull(segment);
        ArgumentNullException.ThrowIfNull(evidence);

        return _engine.Correlate(
            new AnonymousVisualCorrelationSegment(
                segment.SessionId,
                segment.Source,
                segment.Start,
                segment.End),
            evidence.SessionId,
            evidence.Status,
            evidence.Intervals);
    }
}
