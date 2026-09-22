using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.Tests;

public sealed class IpcTests
{
    [Fact]
    public async Task Framing_RoundTrip_PreservesWorkerContract()
    {
        await using var stream = new MemoryStream();
        var request = new WorkerRequest("transcribe", WorkId: "work-1", Pcm16: [1, 2, 3]);
        await PipeFraming.WriteAsync(stream, request);
        stream.Position = 0;
        var restored = await PipeFraming.ReadAsync<WorkerRequest>(stream);
        Assert.Equal(request.Command, restored.Command);
        Assert.Equal(request.WorkId, restored.WorkId);
        Assert.Equal(request.Pcm16, restored.Pcm16);
        Assert.Equal(request.SampleRate, restored.SampleRate);
    }

    [Fact]
    public async Task WorkerClient_InternalConnectDeadline_ThrowsTimeoutException()
    {
        await using var client = new WorkerClient($"trazio-missing-{Guid.NewGuid():N}");

        var exception = await Assert.ThrowsAsync<TimeoutException>(() =>
            client.ConnectAsync(TimeSpan.FromMilliseconds(100), CancellationToken.None));

        Assert.Contains("Se agotó el tiempo de espera", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkerClient_CallerCancellation_RemainsOperationCancelled()
    {
        await using var client = new WorkerClient($"trazio-missing-{Guid.NewGuid():N}");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.ConnectAsync(TimeSpan.FromSeconds(5), cancellation.Token));

        Assert.IsNotType<TimeoutException>(exception);
    }
    [Fact]
    public async Task Framing_InvalidLength_RejectsMessage()
    {
        await using var stream = new MemoryStream([0xFF, 0xFF, 0xFF, 0x7F]);
        await Assert.ThrowsAsync<InvalidDataException>(() => PipeFraming.ReadAsync<WorkerRequest>(stream));
    }

    [Fact]
    public async Task Framing_ClearsSerializedPcmPayloadAfterWrite()
    {
        var cleared = new List<byte[]>();
        PipeFraming.BufferClearedForTests = memory => cleared.Add(memory.ToArray());
        try
        {
            await using var stream = new MemoryStream();
            await PipeFraming.WriteAsync(stream, new WorkerRequest("transcribe", WorkId: "sensitive", Pcm16: [91, 92, 93]));
        }
        finally { PipeFraming.BufferClearedForTests = null; }

        Assert.NotEmpty(cleared);
        Assert.All(cleared.SelectMany(bytes => bytes), value => Assert.Equal(0, value));
    }}

