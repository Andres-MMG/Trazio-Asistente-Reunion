using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.Tests;

public sealed class SegmentAnnotationStoreTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "trazio-annotations-" + Guid.NewGuid().ToString("N"));
    private AesContentProtector _protector = null!;
    private SqliteSessionStore _store = null!;
    private string DatabasePath => Path.Combine(_directory, "annotations.db");

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
        Directory.Delete(_directory, true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task AddAndList_EncryptsTextAndKeepsSegmentProvenance()
    {
        var segment = await CreateSegmentAsync();

        var note = await _store.AddSegmentAnnotationAsync(
            segment.Id,
            SegmentAnnotationKind.Note,
            "Nota privada de seguimiento");
        var decision = await _store.AddSegmentAnnotationAsync(
            segment.Id,
            SegmentAnnotationKind.Decision,
            "Decisión confidencial");
        var loaded = await _store.ListSegmentAnnotationsAsync(segment.SessionId);
        var raw = System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(DatabasePath));

        Assert.Equal([note.Id, decision.Id], loaded.Select(item => item.Id));
        Assert.All(loaded, item =>
        {
            Assert.Equal(segment.Id, item.SegmentId);
            Assert.Equal(segment.SessionId, item.SessionId);
            Assert.Equal(SegmentAnnotationStatus.Open, item.Status);
        });
        Assert.DoesNotContain("Nota privada", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("Decisión confidencial", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetStatus_OnlyAllowsFollowUpAndUsesExpectedState()
    {
        var segment = await CreateSegmentAsync();
        var followUp = await _store.AddSegmentAnnotationAsync(
            segment.Id,
            SegmentAnnotationKind.FollowUp,
            "Enviar el informe");
        var note = await _store.AddSegmentAnnotationAsync(
            segment.Id,
            SegmentAnnotationKind.Note,
            "Contexto");

        var applied = await _store.SetSegmentAnnotationStatusAsync(
            followUp.Id,
            SegmentAnnotationStatus.Open,
            SegmentAnnotationStatus.Completed);
        var stale = await _store.SetSegmentAnnotationStatusAsync(
            followUp.Id,
            SegmentAnnotationStatus.Open,
            SegmentAnnotationStatus.Completed);

        Assert.Equal(SegmentAnnotationWriteStatus.Applied, applied);
        Assert.Equal(SegmentAnnotationWriteStatus.StateChanged, stale);
        Assert.Equal(
            SegmentAnnotationStatus.Completed,
            (await _store.ListSegmentAnnotationsAsync(segment.SessionId)).Single(item => item.Id == followUp.Id).Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.SetSegmentAnnotationStatusAsync(
            note.Id,
            SegmentAnnotationStatus.Open,
            SegmentAnnotationStatus.Completed));
    }

    [Fact]
    public async Task DeleteAndSessionCascade_RemoveOnlyRequestedScope()
    {
        var first = await CreateSegmentAsync();
        var second = await CreateSegmentAsync();
        var removed = await _store.AddSegmentAnnotationAsync(first.Id, SegmentAnnotationKind.Note, "Eliminar");
        await _store.AddSegmentAnnotationAsync(first.Id, SegmentAnnotationKind.Decision, "Conservar hasta borrar sesión");
        var unrelated = await _store.AddSegmentAnnotationAsync(second.Id, SegmentAnnotationKind.Note, "Otra sesión");

        Assert.True(await _store.DeleteSegmentAnnotationAsync(removed.Id));
        Assert.False(await _store.DeleteSegmentAnnotationAsync(removed.Id));
        Assert.Single(await _store.ListSegmentAnnotationsAsync(first.SessionId));

        await _store.DeleteSessionAsync(first.SessionId);

        Assert.Empty(await _store.ListSegmentAnnotationsAsync(first.SessionId));
        Assert.Equal(unrelated.Id, Assert.Single(await _store.ListSegmentAnnotationsAsync(second.SessionId)).Id);
    }

    [Fact]
    public async Task Add_RejectsInvalidOrExcessiveTextBeforeWriting()
    {
        var segment = await CreateSegmentAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => _store.AddSegmentAnnotationAsync(
            segment.Id,
            SegmentAnnotationKind.Note,
            "   "));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _store.AddSegmentAnnotationAsync(
            segment.Id,
            SegmentAnnotationKind.Note,
            new string('x', SegmentAnnotationLimits.MaximumTextLength + 1)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _store.AddSegmentAnnotationAsync(
            segment.Id,
            (SegmentAnnotationKind)99,
            "Texto"));

        Assert.Empty(await _store.ListSegmentAnnotationsAsync(segment.SessionId));
    }

    [Fact]
    public async Task List_CorruptedEncryptedTextFailsClosed()
    {
        var segment = await CreateSegmentAsync();
        var annotation = await _store.AddSegmentAnnotationAsync(
            segment.Id,
            SegmentAnnotationKind.Note,
            "Integridad protegida");
        await using (var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE segment_annotations SET text_cipher=zeroblob(length(text_cipher)) WHERE id=$id";
            command.Parameters.AddWithValue("$id", annotation.Id);
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAnyAsync<CryptographicException>(() =>
            _store.ListSegmentAnnotationsAsync(segment.SessionId));
    }

    [Fact]
    public async Task InitializeAsync_TwicePreservesAnnotationsAndSchema()
    {
        var segment = await CreateSegmentAsync();
        var annotation = await _store.AddSegmentAnnotationAsync(
            segment.Id,
            SegmentAnnotationKind.FollowUp,
            "Pendiente");

        await _store.InitializeAsync();
        await _store.InitializeAsync();

        Assert.Equal(annotation, Assert.Single(await _store.ListSegmentAnnotationsAsync(segment.SessionId)));
    }

    private async Task<TranscriptSegment> CreateSegmentAsync()
    {
        var session = new MeetingSession(
            Guid.NewGuid().ToString("N"),
            "Anotaciones",
            DateTimeOffset.UtcNow,
            null,
            SessionState.Completed);
        await _store.CreateSessionAsync(session);
        var segment = new TranscriptSegment(
            Guid.NewGuid().ToString("N"),
            session.Id,
            AudioSourceKind.SystemOutput,
            0,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(4),
            "Texto",
            DateTimeOffset.UtcNow);
        await _store.SaveSegmentAsync(segment);
        return segment;
    }
}
