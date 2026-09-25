using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.Tests;

public sealed class LocalAsrSettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "trazio-local-asr-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Create_ExistingLlamaCppAndQwenFiles_NormalizesPaths()
    {
        Directory.CreateDirectory(_directory);
        var server = Create("llama-server.exe");
        var model = Create("Qwen3-ASR-0.6B-Q4_K_M.gguf");
        var projector = Create("mmproj-Qwen3-ASR-0.6B-F16.gguf");

        var settings = LocalAsrSettingsPolicy.Create(server, model, projector);

        Assert.Equal(Path.GetFullPath(server), settings.LlamaServerPath);
        Assert.Equal(Path.GetFullPath(model), settings.QwenModelPath);
        Assert.Equal(Path.GetFullPath(projector), settings.MultimodalProjectorPath);
    }

    [Theory]
    [InlineData("llama-server.txt", "model.gguf", "mmproj.gguf")]
    [InlineData("llama-server.exe", "model.bin", "mmproj.gguf")]
    [InlineData("llama-server.exe", "model.gguf", "projector.gguf")]
    public void Create_InvalidFileRoles_RejectsConfiguration(string serverName, string modelName, string projectorName)
    {
        Directory.CreateDirectory(_directory);
        var server = Create(serverName);
        var model = Create(modelName);
        var projector = Create(projectorName);

        Assert.Throws<InvalidOperationException>(() =>
            LocalAsrSettingsPolicy.Create(server, model, projector));
    }

    [Fact]
    public void Create_NetworkExecutablePath_RejectsBeforeAnyNetworkFileProbe()
    {
        Directory.CreateDirectory(_directory);
        var model = Create("model.gguf");
        var projector = Create("mmproj.gguf");

        Assert.Throws<InvalidOperationException>(() =>
            LocalAsrSettingsPolicy.Create(@"\\server\share\llama-server.exe", model, projector));
    }

    private string Create(string name)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, [1]);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}