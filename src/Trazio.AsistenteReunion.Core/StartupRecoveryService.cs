namespace Trazio.AsistenteReunion.Core;

public sealed record RecoverableSession(SessionSummary Session, IReadOnlyList<AudioChunk> PendingAudio);

public sealed class StartupRecoveryService(SqliteSessionStore store)
{
    public async Task<IReadOnlyList<RecoverableSession>> PrepareAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        // This service is called once during application startup, before a new capture can begin.
        await store.NormalizeStaleRecordingSessionsAsync(now, cancellationToken);
        await store.DeleteExpiredPendingAsync(now, cancellationToken);

        var recoverable = new List<RecoverableSession>();
        var interrupted = (await store.ListSessionsAsync(cancellationToken))
            .Where(session => session.State == SessionState.Interrupted)
            .OrderByDescending(session => session.StartedAt);
        foreach (var session in interrupted)
        {
            var pending = await store.GetPendingAsync(session.Id, cancellationToken);
            if (pending.Count > 0) recoverable.Add(new(session, pending));
        }
        return recoverable;
    }
}
