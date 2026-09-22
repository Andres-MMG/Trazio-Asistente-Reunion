using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Trazio.AsistenteReunion.Core;

public sealed class AudioArchiveStore(string rootDirectory, SqliteSessionStore database, IContentProtector protector)
{
    private static readonly byte[] Magic = "TRAZAUD1"u8.ToArray();
    private readonly SemaphoreSlim _files = new(1, 1);

    public AudioArchiveSession CreateSession(string sessionId, long byteBudget) =>
        new(this, sessionId, byteBudget);

    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var session in await database.ListSessionsAsync(cancellationToken))
            foreach (var chunk in await database.GetArchivedAudioAsync(session.Id, null, cancellationToken))
            {
                var path = ResolvePath(chunk.RelativePath);
                if (File.Exists(path)) known.Add(path);
                else await database.DeleteArchivedAudioMetadataAsync(chunk.Id, cancellationToken);
            }
        await _files.WaitAsync(cancellationToken);
        try
        {
            if (!Directory.Exists(rootDirectory)) return;
            foreach (var partial in Directory.EnumerateFiles(rootDirectory, "*.partial.*", SearchOption.AllDirectories)) TryDelete(partial);
            foreach (var file in Directory.EnumerateFiles(rootDirectory, "*.wav.aes", SearchOption.AllDirectories))
                if (!known.Contains(Path.GetFullPath(file))) TryDelete(file);
        }
        finally { _files.Release(); }
    }

    public async Task<byte[]> ReadChunkAsync(ArchivedAudioChunk chunk, CancellationToken cancellationToken = default)
    {
        await _files.WaitAsync(cancellationToken);
        try
        {
            var bytes = await File.ReadAllBytesAsync(ResolvePath(chunk.RelativePath), cancellationToken);
            return Decrypt(bytes, chunk.Id);
        }
        finally { _files.Release(); }
    }

    public async Task ExportWavAsync(string sessionId, AudioSourceKind source, string destination, CancellationToken cancellationToken = default)
    {
        var chunks = await database.GetArchivedAudioAsync(sessionId, source, cancellationToken);
        if (chunks.Count == 0) throw new InvalidOperationException("No hay audio conservado disponible para esta fuente.");
        await _files.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var output = new FileStream(destination, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            output.Position = WavPcm.HeaderLength;
            long pcmBytes = 0;
            foreach (var chunk in chunks)
            {
                var encrypted = await File.ReadAllBytesAsync(ResolvePath(chunk.RelativePath), cancellationToken);
                var wav = Decrypt(encrypted, chunk.Id);
                try
                {
                    var pcm = WavPcm.GetPcm16(wav);
                    await output.WriteAsync(pcm, cancellationToken);
                    pcmBytes += pcm.Length;
                }
                finally { CryptographicOperations.ZeroMemory(wav); }
            }
            output.Position = 0;
            var header = new byte[WavPcm.HeaderLength];
            WavPcm.WriteHeader(header, pcmBytes);
            await output.WriteAsync(header, cancellationToken);
            await output.FlushAsync(cancellationToken);
            output.Flush(true);
        }
        catch
        {
            File.Delete(destination);
            throw;
        }
        finally { _files.Release(); }
    }

    public async Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var chunks = await database.GetArchivedAudioAsync(sessionId, null, cancellationToken);
        await database.DeleteSessionAsync(sessionId, cancellationToken);
        await _files.WaitAsync(cancellationToken);
        try { foreach (var chunk in chunks) TryDelete(ResolvePath(chunk.RelativePath)); }
        finally { _files.Release(); }
    }

    internal async Task<ArchivedAudioChunk> CommitAsync(
        string sessionId, AudioSourceKind source, long sequence, DateTimeOffset startedAt, byte[] pcm16,
        long byteBudget, CancellationToken cancellationToken)
    {
        var id = $"{sessionId}:{source}:archive:{sequence}";
        var relativePath = Path.Combine(sessionId, source.ToString(), $"{sequence:D8}.wav.aes");
        var finalPath = ResolvePath(relativePath);
        var temporary = finalPath + $".partial.{Guid.NewGuid():N}";
        var wav = WavPcm.CreateMono16(pcm16);
        var encrypted = Encrypt(wav, id);
        CryptographicOperations.ZeroMemory(wav);
        await _files.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await file.WriteAsync(encrypted, cancellationToken);
                await file.FlushAsync(cancellationToken);
                file.Flush(true);
            }
            File.Move(temporary, finalPath, false);
            var chunk = new ArchivedAudioChunk(id, sessionId, source, sequence, startedAt,
                TimeSpan.FromSeconds(pcm16.Length / 2d / 16_000), relativePath, encrypted.LongLength);
            try { await database.SaveArchivedAudioAsync(chunk, cancellationToken); }
            catch { TryDelete(finalPath); throw; }
            await PruneCoreAsync(byteBudget, cancellationToken);
            return chunk;
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
        finally { _files.Release(); }
    }

    public async Task PruneAsync(long byteBudget, CancellationToken cancellationToken = default)
    {
        await _files.WaitAsync(cancellationToken);
        try { await PruneCoreAsync(byteBudget, cancellationToken); }
        finally { _files.Release(); }
    }

    private async Task PruneCoreAsync(long byteBudget, CancellationToken cancellationToken)
    {
        var candidates = await database.GetPrunableArchivedAudioAsync(cancellationToken);
        var total = await database.GetTotalArchivedAudioBytesAsync(cancellationToken);
        foreach (var chunk in candidates)
        {
            if (total <= byteBudget) break;
            if (!await database.DeleteArchivedAudioMetadataAsync(chunk.Id, cancellationToken)) continue;
            TryDelete(ResolvePath(chunk.RelativePath));
            total -= chunk.EncryptedBytes;
        }
    }

    private byte[] Encrypt(byte[] plaintext, string id)
    {
        var payload = protector.Protect(plaintext, $"archive:{id}:wav");
        var result = new byte[Magic.Length + 1 + payload.Nonce.Length + payload.Tag.Length + payload.Ciphertext.Length];
        Magic.CopyTo(result, 0);
        result[Magic.Length] = 1;
        payload.Nonce.CopyTo(result, Magic.Length + 1);
        payload.Tag.CopyTo(result, Magic.Length + 13);
        payload.Ciphertext.CopyTo(result, Magic.Length + 29);
        return result;
    }

    private byte[] Decrypt(byte[] bytes, string id)
    {
        const int prefix = 37;
        if (bytes.Length < prefix || !bytes.AsSpan(0, Magic.Length).SequenceEqual(Magic) || bytes[Magic.Length] != 1)
            throw new InvalidDataException("El archivo de audio cifrado no es válido.");
        var payload = new EncryptedPayload(bytes[9..21], bytes[37..], bytes[21..37]);
        return protector.Unprotect(payload, $"archive:{id}:wav");
    }

    private string ResolvePath(string relativePath)
    {
        var root = Path.GetFullPath(rootDirectory) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(rootDirectory, relativePath));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("La ruta del audio archivado no es válida.");
        return full;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (FileNotFoundException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

public sealed class AudioArchiveSession : IAsyncDisposable
{
    public const int ChunkSeconds = 30;
    private const int BytesPerSecond = 16_000 * 2;
    private readonly AudioArchiveStore _store;
    private readonly string _sessionId;
    private readonly long _byteBudget;
    private readonly Dictionary<AudioSourceKind, SourceBuffer> _buffers = [];
    private bool _completed;

    internal AudioArchiveSession(AudioArchiveStore store, string sessionId, long byteBudget)
    {
        _store = store;
        _sessionId = sessionId;
        _byteBudget = byteBudget;
    }

    public event EventHandler<ArchivedAudioChunk>? ChunkCommitted;
    public long BufferedBytes => _buffers.Values.Sum(b => b.Stream.Length);

    public async Task AppendAsync(CapturedAudioData captured, CancellationToken cancellationToken = default)
    {
        if (_completed) throw new InvalidOperationException("La sesión del archivo de audio está cerrada.");
        if (!_buffers.TryGetValue(captured.Source, out var buffer))
            _buffers[captured.Source] = buffer = new(captured.CapturedAt);
        var offset = 0;
        while (offset < captured.Pcm16.Length)
        {
            if (buffer.Stream.Length == 0)
                buffer.StartedAt = captured.CapturedAt.AddSeconds(offset / (double)BytesPerSecond);
            var take = Math.Min(ChunkSeconds * BytesPerSecond - buffer.Stream.LengthAsInt(), captured.Pcm16.Length - offset);
            buffer.Stream.Write(captured.Pcm16, offset, take);
            offset += take;
            if (buffer.Stream.Length == ChunkSeconds * BytesPerSecond) await FlushAsync(captured.Source, buffer, cancellationToken);
        }
    }

    public async Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        if (_completed) return;
        foreach (var pair in _buffers.ToArray())
            if (pair.Value.Stream.Length > 0) await FlushAsync(pair.Key, pair.Value, cancellationToken);
        _completed = true;
    }

    private async Task FlushAsync(AudioSourceKind source, SourceBuffer buffer, CancellationToken cancellationToken)
    {
        var pcm = buffer.Stream.ToArray();
        buffer.Stream.SetLength(0);
        var startedAt = buffer.StartedAt;
        var chunk = await _store.CommitAsync(_sessionId, source, buffer.Sequence++, startedAt, pcm, _byteBudget, cancellationToken);
        CryptographicOperations.ZeroMemory(pcm);
        ChunkCommitted?.Invoke(this, chunk);
    }

    public ValueTask DisposeAsync()
    {
        foreach (var buffer in _buffers.Values)
        {
            if (buffer.Stream.TryGetBuffer(out var bytes)) CryptographicOperations.ZeroMemory(bytes.AsSpan(0, checked((int)buffer.Stream.Length)));
            buffer.Stream.Dispose();
        }
        _completed = true;
        return ValueTask.CompletedTask;
    }

    private sealed class SourceBuffer(DateTimeOffset startedAt)
    {
        public MemoryStream Stream { get; } = new(ChunkSeconds * BytesPerSecond);
        public DateTimeOffset StartedAt { get; set; } = startedAt;
        public long Sequence { get; set; }
    }
}

public sealed record CapturedAudioData(AudioSourceKind Source, byte[] Pcm16, DateTimeOffset CapturedAt);

file static class AudioArchiveExtensions
{
    extension(MemoryStream stream)
    {
        public int LengthAsInt() => checked((int)stream.Length);
    }
}
