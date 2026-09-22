using System.Security.Cryptography;

namespace Trazio.AsistenteReunion.Core;

public sealed record WhisperModelDefinition(string FileName, Uri DownloadUri, long SizeBytes, string Sha256)
{
    public static WhisperModelDefinition Recommended { get; } = new(
        "ggml-base.bin",
        new Uri("https://huggingface.co/ggerganov/whisper.cpp/resolve/5359861c739e955e79d9a303bcbc70fb988958b1/ggml-base.bin"),
        147951465,
        "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe");
}

public sealed record ModelDownloadProgress(long ReceivedBytes, long TotalBytes);

public sealed class WhisperModelStore(HttpClient client, string directory, WhisperModelDefinition definition)
{
    private readonly SemaphoreSlim _downloadLock = new(1, 1);
    public string ModelPath => Path.Combine(directory, definition.FileName);

    public async Task<string?> ResolveAsync(string? preferredPath, string bundledDirectory, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(preferredPath) && File.Exists(preferredPath))
        {
            var isCatalogPath = string.Equals(Path.GetFullPath(preferredPath), Path.GetFullPath(ModelPath), StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFullPath(preferredPath), Path.GetFullPath(Path.Combine(bundledDirectory, definition.FileName)), StringComparison.OrdinalIgnoreCase);
            if (isCatalogPath) await VerifyAsync(preferredPath, cancellationToken);
            return preferredPath;
        }
        foreach (var candidate in new[] { Path.Combine(bundledDirectory, definition.FileName), ModelPath })
        {
            if (!File.Exists(candidate)) continue;
            await VerifyAsync(candidate, cancellationToken);
            return candidate;
        }
        return null;
    }

    public async Task<string> DownloadAsync(IProgress<ModelDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        await _downloadLock.WaitAsync(cancellationToken);
        string? temporary = null;
        try
        {
            if (File.Exists(ModelPath))
            {
                await VerifyAsync(ModelPath, cancellationToken);
                return ModelPath;
            }
            Directory.CreateDirectory(directory);
            temporary = Path.Combine(directory, definition.FileName + "." + Guid.NewGuid().ToString("N") + ".partial");
            using var response = await client.GetAsync(definition.DownloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long length && length != definition.SizeBytes)
                throw new InvalidDataException("La descarga del modelo tiene un tamaño inesperado. Inténtalo nuevamente.");
            await TransferVerifiedAsync(response.Content, temporary, progress, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, ModelPath, overwrite: false);
            temporary = null;
            return ModelPath;
        }
        finally
        {
            if (temporary is not null && File.Exists(temporary)) File.Delete(temporary);
            _downloadLock.Release();
        }
    }

    private async Task TransferVerifiedAsync(HttpContent content, string temporary, IProgress<ModelDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        await using var input = await content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) != 0)
        {
            total += read;
            if (total > definition.SizeBytes) throw new InvalidDataException("La descarga del modelo supera el tamaño esperado.");
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            progress?.Report(new(total, definition.SizeBytes));
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (total != definition.SizeBytes || !HashMatches(hash.GetHashAndReset()))
            throw new InvalidDataException("La descarga del modelo no superó la verificación de integridad. Inténtalo nuevamente.");
        await output.FlushAsync(cancellationToken);
    }

    private async Task VerifyAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != definition.SizeBytes || !HashMatches(await SHA256.HashDataAsync(stream, cancellationToken)))
            throw new InvalidDataException("El modelo recomendado está dañado. Retira el archivo dañado de la carpeta de modelos e inténtalo nuevamente, o selecciona un modelo personalizado.");
    }

    private bool HashMatches(byte[] hash) => CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(definition.Sha256));
}
