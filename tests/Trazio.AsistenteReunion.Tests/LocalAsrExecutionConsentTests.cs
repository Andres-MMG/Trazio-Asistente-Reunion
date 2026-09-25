using System.Security.Cryptography;
using Trazio.AsistenteReunion.App;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.Tests;

public sealed class LocalAsrExecutionConsentTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(),
        "trazio-qwen-consent-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task StartAsync_DeniedConsent_NeverStartsExecutable()
    {
        var (server, model, projector) = CreateFiles();
        var called = false;
        var factory = new LlamaCppAsrTransportFactory(
            LlamaCppAsrRuntimeOptions.Create(server, projector),
            (preview, _) =>
            {
                called = true;
                Assert.Equal(server, preview.ServerExecutablePath);
                Assert.Equal(model, preview.ModelPath);
                Assert.Equal(projector, preview.MultimodalProjectorPath);
                Assert.Equal(Convert.ToHexString(SHA256.HashData([1, 2, 3])), preview.ServerSha256);
                Assert.Equal(Convert.ToHexString(SHA256.HashData([4, 5, 6])), preview.ModelSha256);
                Assert.Equal(Convert.ToHexString(SHA256.HashData([7, 8, 9])), preview.MultimodalProjectorSha256);
                return Task.FromResult(false);
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            factory.StartAsync(model, "es", CancellationToken.None));
        Assert.True(called);
    }

    [Fact]
    public async Task StartAsync_CancellationDuringConsent_NeverStartsExecutable()
    {
        var (server, model, projector) = CreateFiles();
        using var cancellation = new CancellationTokenSource();
        var factory = new LlamaCppAsrTransportFactory(
            LlamaCppAsrRuntimeOptions.Create(server, projector),
            (_, _) =>
            {
                cancellation.Cancel();
                return Task.FromResult(true);
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            factory.StartAsync(model, "es", cancellation.Token));
    }

    [Fact]
    public async Task StartAsync_FilesCannotBeNormallyChangedWhileConsentIsOpen()
    {
        var (server, model, projector) = CreateFiles();
        var factory = new LlamaCppAsrTransportFactory(
            LlamaCppAsrRuntimeOptions.Create(server, projector),
            (_, _) =>
            {
                foreach (var path in new[] { server, model, projector })
                {
                    Assert.Throws<IOException>(() => File.WriteAllBytes(path, [0]));
                    Assert.Throws<IOException>(() => File.Delete(path));
                }
                return Task.FromResult(false);
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            factory.StartAsync(model, "es", CancellationToken.None));
    }

    [Fact]
    public void ValidateLocalFile_RejectsNetworkAndRelativePaths()
    {
        Assert.Throws<InvalidOperationException>(() =>
            LocalAsrExecutionGuard.ValidateLocalFile(@"\\server\share\llama-server.exe"));
        Assert.Throws<InvalidOperationException>(() =>
            LocalAsrExecutionGuard.ValidateLocalFile("llama-server.exe"));
    }

    [Fact]
    public void TrustWarning_DisclosesExternalCodeAndNetworkRisk()
    {
        Assert.Contains("programa local externo", LocalAsrExecutionPreview.TrustWarning);
        Assert.Contains("conectarse a Internet", LocalAsrExecutionPreview.TrustWarning);
        Assert.Contains("NO garantiza aislamiento", LocalAsrExecutionPreview.TrustWarning);
    }

    [Fact]
    public void BuildDetails_IdentifiesAudioSourceIntervalAndEveryHash()
    {
        var preview = new LocalAsrExecutionPreview("server.exe", "AA", "model.gguf", "BB", "mmproj.gguf", "CC");

        var details = LocalAsrExecutionConsentDialog.BuildDetails(
            preview, AudioSourceKind.SystemOutput, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(21));

        Assert.Contains("Audio del equipo", details);
        Assert.Contains("00:00:15", details);
        Assert.Contains("00:00:21", details);
        Assert.Contains("server.exe", details);
        Assert.Contains("model.gguf", details);
        Assert.Contains("mmproj.gguf", details);
        Assert.Contains("AA", details);
        Assert.Contains("BB", details);
        Assert.Contains("CC", details);
    }

    private (string Server, string Model, string Projector) CreateFiles()
    {
        Directory.CreateDirectory(_directory);
        var server = Path.Combine(_directory, "llama-server.exe");
        var model = Path.Combine(_directory, "qwen3-asr.gguf");
        var projector = Path.Combine(_directory, "mmproj-qwen3-asr.gguf");
        File.WriteAllBytes(server, [1, 2, 3]);
        File.WriteAllBytes(model, [4, 5, 6]);
        File.WriteAllBytes(projector, [7, 8, 9]);
        return (server, model, projector);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
