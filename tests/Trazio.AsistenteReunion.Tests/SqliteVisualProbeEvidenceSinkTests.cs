using System.Security.Cryptography;
using Trazio.AsistenteReunion.App;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.Tests;

public sealed class SqliteVisualProbeEvidenceSinkTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "trazio-probe-sink-tests-" + Guid.NewGuid().ToString("N"));
    private AesContentProtector _protector = null!;
    private SqliteSessionStore _store = null!;
    private string DatabasePath => Path.Combine(_directory, "test.db");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        _protector = new(RandomNumberGenerator.GetBytes(32));
        _store = new(DatabasePath, _protector);
        await _store.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        _protector.Dispose();
        Directory.Delete(_directory, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public async Task WriteAsync_RealEncryptedStore_RoundTripsAndDeduplicates()
    {
        var session = NewSession();
        await _store.CreateSessionAsync(session);
        var sink = new SqliteVisualProbeEvidenceSink(session.Id, _store);
        var interval = Coverage(session.Id);

        await sink.WriteAsync(interval, CancellationToken.None);
        await sink.WriteAsync(interval, CancellationToken.None);

        var restored = await _store.GetAnonymousVisualEvidenceAsync(session.Id);
        Assert.Equal(AnonymousVisualEvidenceReadStatus.Loaded, restored.Status);
        Assert.Equal(interval, Assert.Single(restored.Intervals));
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public async Task WriteAsync_ForDifferentSession_FailsInsideVisualSinkBoundary()
    {
        var session = NewSession();
        await _store.CreateSessionAsync(session);
        var sink = new SqliteVisualProbeEvidenceSink(session.Id, _store);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sink.WriteAsync(Coverage("different-session"), CancellationToken.None).AsTask());

        var restored = await _store.GetAnonymousVisualEvidenceAsync(session.Id);
        Assert.Empty(restored.Intervals);
    }

    private static MeetingSession NewSession() => new(
        Guid.NewGuid().ToString("N"),
        "Visual probe sink",
        DateTimeOffset.UtcNow,
        null,
        SessionState.Recording);

    private static AnonymousVisualEvidenceInterval Coverage(string sessionId) =>
        AnonymousVisualEvidenceInterval.Coverage(
            Guid.ParseExact("1234567890abcdef1234567890abcdef", "N"),
            sessionId,
            TimeSpan.FromTicks(12_345_678),
            TimeSpan.FromTicks(98_765_432),
            AnonymousVisualAnalysisAvailability.Unavailable,
            confidence: 0,
            new(
                MeetingProvider.GoogleMeet,
                AnonymousVisualEvidenceVersions.CurrentProfile,
                AnonymousVisualEvidenceVersions.CurrentEvidence,
                AnonymousVisualEvidenceVersions.CurrentDetector,
                AnonymousVisualEvidenceVersions.CurrentPolicy));
}
