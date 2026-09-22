using System.Security.Cryptography;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.Tests;

public sealed class AudioArchiveStoreTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "trazio-audio-" + Guid.NewGuid().ToString("N"));
    private AesContentProtector _protector = null!;
    private SqliteSessionStore _database = null!;
    private AudioArchiveStore _archive = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        _protector = new(RandomNumberGenerator.GetBytes(32));
        _database = new(Path.Combine(_directory, "test.db"), _protector);
        await _database.InitializeAsync();
        _archive = new(Path.Combine(_directory, "audio"), _database, _protector);
    }

    public Task DisposeAsync()
    {
        _protector.Dispose();
        Directory.Delete(_directory, true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task CompleteAsync_EncryptsWavAndDecryptsExactAudio()
    {
        var session = await CreateSessionAsync();
        var pcm = Enumerable.Range(0, 32_000).Select(i => (byte)(i % 251)).ToArray();
        await using var writer = _archive.CreateSession(session.Id, 1024 * 1024);
        await writer.AppendAsync(new(AudioSourceKind.Microphone, pcm, session.StartedAt));
        await writer.CompleteAsync();

        var metadata = Assert.Single(await _database.GetArchivedAudioAsync(session.Id, AudioSourceKind.Microphone));
        var encrypted = await File.ReadAllBytesAsync(Path.Combine(_directory, "audio", metadata.RelativePath));
        Assert.False(encrypted.AsSpan(0, 4).SequenceEqual("RIFF"u8));
        Assert.Equal(pcm, WavPcm.GetPcm16(await _archive.ReadChunkAsync(metadata)).ToArray());
    }

    [Fact]
    public async Task AppendAsync_ThirtyOneSeconds_CommitsThirtyAndFlushesOne()
    {
        var session = await CreateSessionAsync();
        await using var writer = _archive.CreateSession(session.Id, 10 * 1024 * 1024);
        await writer.AppendAsync(new(AudioSourceKind.Microphone, new byte[31 * 32_000], session.StartedAt));
        Assert.Equal(32_000, writer.BufferedBytes);
        await writer.CompleteAsync();

        var chunks = await _database.GetArchivedAudioAsync(session.Id, AudioSourceKind.Microphone);
        Assert.Equal(2, chunks.Count);
        Assert.Equal(TimeSpan.FromSeconds(30), chunks[0].Duration);
        Assert.Equal(TimeSpan.FromSeconds(1), chunks[1].Duration);
    }

    [Fact]
    public async Task AppendAsync_TwoSources_ArchivesIndependentSequences()
    {
        var session = await CreateSessionAsync();
        await using var writer = _archive.CreateSession(session.Id, 10 * 1024 * 1024);
        await writer.AppendAsync(new(AudioSourceKind.Microphone, new byte[40_000], session.StartedAt));
        await writer.AppendAsync(new(AudioSourceKind.SystemOutput, new byte[64_000], session.StartedAt));
        await writer.CompleteAsync();

        var chunks = await _database.GetArchivedAudioAsync(session.Id);
        Assert.Equal(2, chunks.Count);
        Assert.Contains(chunks, c => c.Source == AudioSourceKind.Microphone && c.Sequence == 0);
        Assert.Contains(chunks, c => c.Source == AudioSourceKind.SystemOutput && c.Sequence == 0);
    }

    [Fact]
    public async Task PruneAsync_DeletesCompletedAudioButRetainsTranscriptAndActiveAudio()
    {
        var old = await CreateSessionAsync(SessionState.Completed);
        await _database.SaveSegmentAsync(new("seg", old.Id, AudioSourceKind.Microphone, 0, TimeSpan.Zero, TimeSpan.FromSeconds(1), "retain me", DateTimeOffset.UtcNow));
        await WriteSecondAsync(old);
        var active = await CreateSessionAsync(SessionState.Recording);
        await WriteSecondAsync(active);

        await _archive.PruneAsync(1);

        Assert.Empty(await _database.GetArchivedAudioAsync(old.Id));
        Assert.Single(await _database.GetSegmentsAsync(old.Id));
        Assert.Single(await _database.GetArchivedAudioAsync(active.Id));
    }

    [Fact]
    public async Task ExportWavAsync_StreamsAllChunksIntoValidPlaintextWav()
    {
        var session = await CreateSessionAsync();
        await using (var writer = _archive.CreateSession(session.Id, 10 * 1024 * 1024))
        {
            await writer.AppendAsync(new(AudioSourceKind.SystemOutput, new byte[31 * 32_000], session.StartedAt));
            await writer.CompleteAsync();
        }
        var export = Path.Combine(_directory, "export.wav");
        await _archive.ExportWavAsync(session.Id, AudioSourceKind.SystemOutput, export);
        var bytes = await File.ReadAllBytesAsync(export);
        Assert.True(bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8));
        Assert.Equal(31 * 32_000, WavPcm.GetPcm16(bytes).Length);
        Assert.Empty(Directory.GetFiles(_directory, "export.wav.partial.*", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task ExportWavAsync_WhenSourceReadFails_DeletesChosenPlaintextPathWithoutSidecar()
    {
        var session = await CreateSessionAsync();
        await WriteSecondAsync(session);
        var chunk = Assert.Single(await _database.GetArchivedAudioAsync(session.Id));
        await File.WriteAllBytesAsync(Path.Combine(_directory, "audio", chunk.RelativePath), [1, 2, 3]);
        var export = Path.Combine(_directory, "chosen.wav");

        await Assert.ThrowsAsync<InvalidDataException>(() => _archive.ExportWavAsync(session.Id, AudioSourceKind.Microphone, export));

        Assert.False(File.Exists(export));
        Assert.Empty(Directory.GetFiles(_directory, "chosen.wav.*", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task DeleteSessionAsync_RemovesDatabaseFirstAndReconcileDeletesEncryptedOrphan()
    {
        var session = await CreateSessionAsync(SessionState.Completed);
        await WriteSecondAsync(session);
        var chunk = Assert.Single(await _database.GetArchivedAudioAsync(session.Id));
        var path = Path.Combine(_directory, "audio", chunk.RelativePath);
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await _archive.DeleteSessionAsync(session.Id);
            Assert.Empty(await _database.ListSessionsAsync());
            Assert.Empty(await _database.GetArchivedAudioAsync(session.Id));
            Assert.True(File.Exists(path));
        }

        await _archive.ReconcileAsync();
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task ReconcileAsync_WhenCommittedFileIsMissing_RemovesStaleMetadata()
    {
        var session = await CreateSessionAsync(SessionState.Completed);
        await WriteSecondAsync(session);
        var chunk = Assert.Single(await _database.GetArchivedAudioAsync(session.Id));
        File.Delete(Path.Combine(_directory, "audio", chunk.RelativePath));

        await _archive.ReconcileAsync();

        Assert.Empty(await _database.GetArchivedAudioAsync(session.Id));
    }

    [Fact]
    public async Task AppendAsync_RepeatedFullChunks_NeverBuffersMoreThanOneChunkPerSource()
    {
        var session = await CreateSessionAsync();
        await using var writer = _archive.CreateSession(session.Id, 100 * 1024 * 1024);
        for (var i = 0; i < 12; i++)
        {
            await writer.AppendAsync(new(AudioSourceKind.Microphone, new byte[32_000 * 5], session.StartedAt.AddSeconds(i * 5)));
            Assert.InRange(writer.BufferedBytes, 0, AudioArchiveSession.ChunkSeconds * 32_000 - 1);
        }
        await writer.CompleteAsync();
        Assert.Equal(2, (await _database.GetArchivedAudioAsync(session.Id)).Count);
    }

    private async Task WriteSecondAsync(MeetingSession session)
    {
        await using var writer = _archive.CreateSession(session.Id, long.MaxValue);
        await writer.AppendAsync(new(AudioSourceKind.Microphone, new byte[32_000], session.StartedAt));
        await writer.CompleteAsync();
    }

    private async Task<MeetingSession> CreateSessionAsync(SessionState state = SessionState.Recording)
    {
        var session = new MeetingSession(Guid.NewGuid().ToString("N"), "Meeting", DateTimeOffset.UtcNow, state == SessionState.Recording ? null : DateTimeOffset.UtcNow, state);
        await _database.CreateSessionAsync(session);
        return session;
    }
}
