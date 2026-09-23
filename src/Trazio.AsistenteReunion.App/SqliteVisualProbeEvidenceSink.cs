using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.App;

internal sealed class SqliteVisualProbeEvidenceSink : IVisualProbeEvidenceSink
{
    private readonly string _sessionId;
    private readonly SqliteSessionStore _store;
    private readonly Action<string>? _evidencePersisted;

    public SqliteVisualProbeEvidenceSink(
        string sessionId,
        SqliteSessionStore store,
        Action<string>? evidencePersisted = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        _sessionId = sessionId;
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _evidencePersisted = evidencePersisted;
    }

    public async ValueTask WriteAsync(
        AnonymousVisualEvidenceInterval interval,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(interval);
        if (!string.Equals(interval.SessionId, _sessionId, StringComparison.Ordinal))
            throw new InvalidOperationException("Visual evidence belongs to a different session.");

        var inserted = await _store.SaveAnonymousVisualEvidenceAsync(interval, cancellationToken)
            .ConfigureAwait(false);
        if (!inserted || _evidencePersisted is null) return;

        try { _evidencePersisted(_sessionId); }
        catch { }
    }
}
