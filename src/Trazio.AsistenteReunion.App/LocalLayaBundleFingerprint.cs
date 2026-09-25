using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.App;

internal interface ILocalLayaBundleLease : IAsyncDisposable
{
    string Fingerprint { get; }
    Task VerifyUnchangedAsync(CancellationToken cancellationToken);
}

internal interface ILocalLayaBundleFingerprinter
{
    Task<ILocalLayaBundleLease> AcquireAsync(LocalLayaSettings settings, CancellationToken cancellationToken);
}

internal sealed class LocalLayaBundleFingerprinter : ILocalLayaBundleFingerprinter
{
    private static readonly (string Prefix, string RelativePath)[] BundleFiles =
    [
        ("model", "laya.onnx"),
        ("model", "laya.onnx.data"),
        ("model", "laya_config.json"),
        ("model", "tokenizer/tokenizer.json"),
        ("model", "tokenizer/tokenizer_config.json"),
        ("sidecar", "server.mjs"),
        ("sidecar", "package.json"),
        ("sidecar", "package-lock.json")
    ];

    public async Task<ILocalLayaBundleLease> AcquireAsync(
        LocalLayaSettings settings, CancellationToken cancellationToken)
    {
        var files = new List<(string Name, FileStream Stream)>();
        try
        {
            foreach (var (prefix, relativePath) in BundleFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directory = prefix == "model" ? settings.ModelDirectory : settings.SidecarDirectory;
                var path = Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar));
                var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    bufferSize: 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (stream.Length == 0)
                {
                    await stream.DisposeAsync();
                    throw new InvalidDataException("El paquete local Laya contiene un archivo vacío.");
                }
                files.Add(($"{prefix}/{relativePath}", stream));
            }
            var fingerprint = await LocalLayaBundleLease.ComputeAsync(files, cancellationToken);
            return new LocalLayaBundleLease(files, fingerprint);
        }
        catch
        {
            foreach (var (_, stream) in files) await stream.DisposeAsync();
            throw;
        }
    }
}

internal sealed class LocalLayaBundleLease(
    IReadOnlyList<(string Name, FileStream Stream)> files, string fingerprint)
    : ILocalLayaBundleLease
{
    public string Fingerprint { get; } = fingerprint;

    public async Task VerifyUnchangedAsync(CancellationToken cancellationToken)
    {
        var fresh = await ComputeAsync(files, cancellationToken);
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(Fingerprint.AsSpan(7)), Convert.FromHexString(fresh.AsSpan(7))))
            throw new InvalidDataException("Los archivos locales de Laya cambiaron durante la carga o evaluación.");
    }

    internal static async Task<string> ComputeAsync(
        IReadOnlyList<(string Name, FileStream Stream)> files, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            foreach (var (name, stream) in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var label = Encoding.UTF8.GetBytes(name);
                var header = new byte[8];
                BinaryPrimitives.WriteInt64BigEndian(header, label.Length);
                hash.AppendData(header);
                hash.AppendData(label);
                BinaryPrimitives.WriteInt64BigEndian(header, stream.Length);
                hash.AppendData(header);
                stream.Position = 0;
                int read;
                while ((read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
                    hash.AppendData(buffer.AsSpan(0, read));
            }
            return "sha256:" + Convert.ToHexString(hash.GetHashAndReset());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var (_, stream) in files) await stream.DisposeAsync();
    }
}
