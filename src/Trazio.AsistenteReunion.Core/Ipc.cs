using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;

namespace Trazio.AsistenteReunion.Core;

public sealed record WorkerRequest(string Command, string? ModelPath = null, string Language = "es", string? WorkId = null, byte[]? Pcm16 = null, int SampleRate = 16_000, string? InitialPrompt = null);
public sealed record WorkerResponse(bool Success, string? Error = null, string? WorkId = null, IReadOnlyList<WorkerSegmentDto>? Segments = null, string? ModelHash = null);
public sealed record WorkerSegmentDto(long StartMilliseconds, long EndMilliseconds, string Text);

public static class PipeFraming
{
    public const int MaxMessageBytes = 32 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    internal static Action<ReadOnlyMemory<byte>>? BufferClearedForTests { get; set; }

    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        var header = new byte[4];
        try
        {
            if (payload.Length > MaxMessageBytes) throw new InvalidDataException("El mensaje interno es demasiado grande.");
            BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
            await stream.WriteAsync(header, cancellationToken);
            await stream.WriteAsync(payload, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
            BufferClearedForTests?.Invoke(payload);
            CryptographicOperations.ZeroMemory(header);
        }
    }

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken = default)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaxMessageBytes) throw new InvalidDataException("La longitud del mensaje interno no es válida.");
        var payload = new byte[length];
        try
        {
            await stream.ReadExactlyAsync(payload, cancellationToken);
            return JsonSerializer.Deserialize<T>(payload, JsonOptions) ?? throw new InvalidDataException("El mensaje interno JSON no es válido.");
        }
        finally { CryptographicOperations.ZeroMemory(payload); BufferClearedForTests?.Invoke(payload); }
    }
}

public sealed class WorkerClient(string pipeName) : IAsyncDisposable
{
    private NamedPipeClientStream? _pipe;

    public async Task ConnectAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        _pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, System.Security.Principal.TokenImpersonationLevel.Identification);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await _pipe.ConnectAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Se agotó el tiempo de espera al conectar con el proceso de transcripción después de {timeout}.", ex);
        }
    }

    public async Task<WorkerResponse> SendAsync(WorkerRequest request, CancellationToken cancellationToken = default)
    {
        if (_pipe is null || !_pipe.IsConnected) throw new InvalidOperationException("El proceso de transcripción no está conectado.");
        await PipeFraming.WriteAsync(_pipe, request, cancellationToken);
        return await PipeFraming.ReadAsync<WorkerResponse>(_pipe, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_pipe is not null) await _pipe.DisposeAsync();
    }
}



