using System.Security.Cryptography;
using System.IO.Pipes;
using Trazio.AsistenteReunion.Core;
using Whisper.net;

if (args.Length != 2 || args[0] != "--pipe")
{
    Console.Error.WriteLine("Uso: Trazio.AsistenteReunion.Worker --pipe <nombre>");
    return 2;
}

using var runningMarker = ApplicationRunningMarker.Create();
await new WorkerServer(args[1]).RunAsync();
return 0;

internal sealed class WorkerServer(string pipeName)
{
    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.WaitForConnectionAsync(cancellationToken);
        while (pipe.IsConnected && !cancellationToken.IsCancellationRequested)
        {
            WorkerRequest request;
            try { request = await PipeFraming.ReadAsync<WorkerRequest>(pipe, cancellationToken); }
            catch (EndOfStreamException) { break; }
            catch (IOException) { break; }
            try
            {
                var response = await HandleAsync(request, cancellationToken);
                await PipeFraming.WriteAsync(pipe, response, cancellationToken);
                if (request.Command.Equals("stop", StringComparison.OrdinalIgnoreCase)) break;
            }
            finally
            {
                if (request.Pcm16 is not null) CryptographicOperations.ZeroMemory(request.Pcm16);
            }
        }
    }

    private async Task<WorkerResponse> HandleAsync(WorkerRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return request.Command.ToLowerInvariant() switch
            {
                "health" => new(true),
                "start" => await StartAsync(request, cancellationToken),
                "transcribe" => await TranscribeAsync(request, cancellationToken),
                "stop" => Stop(),
                _ => new(false, "Comando desconocido.", request.WorkId)
            };
        }
        catch (Exception ex) { return new(false, ex.Message, request.WorkId); }
    }

    private async Task<WorkerResponse> StartAsync(WorkerRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ModelPath) || !File.Exists(request.ModelPath))
            return new(false, "No se encontró el modelo Whisper configurado.");
        if (request.InitialPrompt is { Length: > GlossaryPromptPlanner.MaximumPromptCharacters })
            return new(false, "El prompt del diccionario supera el límite permitido.");
        if (request.InitialPrompt?.Any(char.IsControl) == true)
            return new(false, "El prompt del diccionario contiene caracteres de control no permitidos.");
        DisposeModel();
        _factory = WhisperFactory.FromPath(request.ModelPath);
        _processor = CreateProcessor(_factory, request);
        await using var model = new FileStream(request.ModelPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        try
        {
            int read;
            while ((read = await model.ReadAsync(buffer, cancellationToken)) > 0) hash.AppendData(buffer, 0, read);
            var verifiedHash = Convert.ToHexString(hash.GetHashAndReset());
            DisposeModel();
            _factory = WhisperFactory.FromPath(request.ModelPath);
            _processor = CreateProcessor(_factory, request);
            return new(true, ModelHash: verifiedHash);
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    private async Task<WorkerResponse> TranscribeAsync(WorkerRequest request, CancellationToken cancellationToken)
    {
        if (_processor is null) return new(false, "El modelo del proceso de transcripción no está cargado.", request.WorkId);
        if (request.Pcm16 is null || request.Pcm16.Length == 0) return new(false, "El audio está vacío.", request.WorkId);
        if (request.SampleRate != 16_000) return new(false, "Solo se acepta audio PCM de 16 kHz.", request.WorkId);
        await using var wav = WaveStreamFactory.CreatePcm16Mono(request.Pcm16, request.SampleRate);
        try
        {
            var segments = new List<WorkerSegmentDto>();
            await foreach (var segment in _processor.ProcessAsync(wav, cancellationToken))
                segments.Add(new((long)segment.Start.TotalMilliseconds, (long)segment.End.TotalMilliseconds, segment.Text.Trim()));
            return new(true, WorkId: request.WorkId, Segments: segments);
        }
        finally
        {
            if (wav.TryGetBuffer(out var serializedWav)) CryptographicOperations.ZeroMemory(serializedWav.AsSpan());
        }
    }

    private static WhisperProcessor CreateProcessor(WhisperFactory factory, WorkerRequest request)
    {
        var builder = factory.CreateBuilder().WithLanguage(request.Language);
        if (!string.IsNullOrWhiteSpace(request.InitialPrompt)) builder.WithPrompt(request.InitialPrompt);
        return builder.Build();
    }

    private WorkerResponse Stop() { DisposeModel(); return new(true); }
    private void DisposeModel()
    {
        _processor?.Dispose();
        _factory?.Dispose();
        _processor = null;
        _factory = null;
    }
}

internal static class WaveStreamFactory
{
    public static MemoryStream CreatePcm16Mono(byte[] pcm, int sampleRate)
    {
        var stream = new MemoryStream(44 + pcm.Length);
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
        {
            writer.Write("RIFF"u8); writer.Write(36 + pcm.Length); writer.Write("WAVE"u8);
            writer.Write("fmt "u8); writer.Write(16); writer.Write((short)1); writer.Write((short)1);
            writer.Write(sampleRate); writer.Write(sampleRate * 2); writer.Write((short)2); writer.Write((short)16);
            writer.Write("data"u8); writer.Write(pcm.Length); writer.Write(pcm);
        }
        stream.Position = 0;
        return stream;
    }
}


