using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.App;

internal sealed class SqliteVisualProbeEvidenceSink : IVisualProbeEvidenceSink
{
    private readonly string _sessionId;
    private readonly SqliteSessionStore _store;

    public SqliteVisualProbeEvidenceSink(string sessionId, SqliteSessionStore store)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        _sessionId = sessionId;
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async ValueTask WriteAsync(
        AnonymousVisualEvidenceInterval interval,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(interval);
        if (!string.Equals(interval.SessionId, _sessionId, StringComparison.Ordinal))
            throw new InvalidOperationException("Visual evidence belongs to a different session.");

        _ = await _store.SaveAnonymousVisualEvidenceAsync(interval, cancellationToken)
            .ConfigureAwait(false);
    }
}
