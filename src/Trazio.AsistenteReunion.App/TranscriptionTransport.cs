using System.Diagnostics;
using System.IO;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.App;

public interface ITranscriptionTransport : IAsyncDisposable
{
    string? VerifiedModelHash { get; }
    Task<WorkerResponse> TranscribeAsync(string workId, byte[] pcm16, CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}

public interface ITranscriptionTransportFactory
{
    Task<ITranscriptionTransport> StartAsync(string modelPath, string language, CancellationToken cancellationToken);
}

public sealed class ProcessTranscriptionTransportFactory : ITranscriptionTransportFactory
{
    public async Task<ITranscriptionTransport> StartAsync(string modelPath, string language, CancellationToken cancellationToken)
    {
        var pipeName = $"trazio-asistente-reunion-{Environment.ProcessId}-{Guid.NewGuid():N}";
        var executable = Path.Combine(AppContext.BaseDirectory, "Trazio.AsistenteReunion.Worker.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("Falta el proceso de transcripción. Reinstala la aplicación.", executable);
        var process = Process.Start(new ProcessStartInfo(executable, $"--pipe {pipeName}") { UseShellExecute = false, CreateNoWindow = true })
            ?? throw new InvalidOperationException("No se pudo iniciar el proceso de transcripción.");
        var client = new WorkerClient(pipeName);
        try
        {
            await client.ConnectAsync(TimeSpan.FromSeconds(10), cancellationToken);
            var response = await client.SendAsync(new("start", modelPath, language), cancellationToken);
            if (!response.Success) throw new InvalidOperationException(response.Error);
            return new ProcessTranscriptionTransport(process, client, response.ModelHash);
        }
        catch
        {
            await client.DisposeAsync();
            TryKill(process);
            throw;
        }
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(true); } catch { }
        process.Dispose();
    }

    private sealed class ProcessTranscriptionTransport(Process process, WorkerClient client, string? verifiedModelHash) : ITranscriptionTransport
    {
        public string? VerifiedModelHash { get; } = verifiedModelHash;
        public Task<WorkerResponse> TranscribeAsync(string workId, byte[] pcm16, CancellationToken cancellationToken) =>
            client.SendAsync(new("transcribe", WorkId: workId, Pcm16: pcm16), cancellationToken);

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            try { await client.SendAsync(new("stop"), cancellationToken); } catch { }
        }

        public async ValueTask DisposeAsync()
        {
            await client.DisposeAsync();
            TryKill(process);
        }
    }
}



