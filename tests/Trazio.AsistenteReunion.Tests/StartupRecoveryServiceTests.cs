using System.Security.Cryptography;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.Tests;

public sealed class StartupRecoveryServiceTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "trazio-startup-" + Guid.NewGuid().ToString("N"));
    private AesContentProtector _protector = null!;
    private SqliteSessionStore _store = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        _protector = new(RandomNumberGenerator.GetBytes(32));
        _store = new(Path.Combine(_directory, "test.db"), _protector);
        await _store.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        _protector.Dispose();
        Directory.Delete(_directory, true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task PrepareAsync_StaleRecordingWithExpiredAudio_NormalizesThenExpiresAudio()
    {
        var now = DateTimeOffset.UtcNow;
        var session = NewRecordingSession(now.AddDays(-2));
        await _store.CreateSessionAsync(session);
        await _store.SavePendingAsync(AudioChunk.Create(session.Id, AudioSourceKind.Microphone, 1, now.AddDays(-2), [1]), now.AddHours(-1));

        var recoverable = await new StartupRecoveryService(_store).PrepareAsync(now);

        Assert.Empty(recoverable);
        var normalized = Assert.Single(await _store.ListSessionsAsync());
        Assert.Equal(SessionState.Interrupted, normalized.State);
        Assert.Equal(now, normalized.EndedAt);
        Assert.Empty(await _store.GetPendingAsync(session.Id));
    }

    [Fact]
    public async Task PrepareAsync_DeclinedRecentRecovery_RemainsInterruptedAndRecoverable()
    {
        var now = DateTimeOffset.UtcNow;
        var session = NewRecordingSession(now.AddMinutes(-20));
        await _store.CreateSessionAsync(session);
        await _store.SavePendingAsync(AudioChunk.Create(session.Id, AudioSourceKind.SystemOutput, 1, now.AddMinutes(-19), [2]), now.AddHours(23));

        var firstPrompt = Assert.Single(await new StartupRecoveryService(_store).PrepareAsync(now));
        Assert.Equal(SessionState.Interrupted, firstPrompt.Session.State);
        // Decline means no recovery mutation. A later startup must still see the same recoverable session.
        var secondPrompt = Assert.Single(await new StartupRecoveryService(_store).PrepareAsync(now.AddMinutes(1)));

        Assert.Equal(session.Id, secondPrompt.Session.Id);
        Assert.Equal(SessionState.Interrupted, Assert.Single(await _store.ListSessionsAsync()).State);
        Assert.Single(await _store.GetPendingAsync(session.Id));
    }

    private static MeetingSession NewRecordingSession(DateTimeOffset startedAt) =>
        new(Guid.NewGuid().ToString("N"), "Interrupted meeting", startedAt, null, SessionState.Recording);
}
