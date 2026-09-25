using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Trazio.AsistenteReunion.App;

namespace Trazio.AsistenteReunion.Tests;

public sealed class LlamaCppAsrTransportTests
{
    [Fact]
    public async Task TranscribeAsync_SendsWavToLoopbackCompatibleChatEndpoint()
    {
        byte[]? capturedPayload = null;
        var handler = new DelegateHandler(async (request, cancellationToken) =>
        {
            capturedPayload = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            return JsonResponse("language Spanish<asr_text>hola desde Qwen");
        });
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://127.0.0.1:8080/")
        };
        var client = new LlamaCppChatAsrClient(httpClient, "qwen3-asr", "Spanish", "test-secret", () => true);

        var response = await client.TranscribeAsync("work-1", new byte[32_000], CancellationToken.None);

        Assert.True(response.Success);
        var segment = Assert.Single(response.Segments!);
        Assert.Equal("hola desde Qwen", segment.Text);
        Assert.Equal(1_000, segment.EndMilliseconds);
        using var requestDocument = JsonDocument.Parse(capturedPayload!);
        var root = requestDocument.RootElement;
        Assert.Equal("qwen3-asr", root.GetProperty("model").GetString());
        Assert.Equal("input_audio", root.GetProperty("messages")[0].GetProperty("content")[0].GetProperty("type").GetString());
        var wav = root.GetProperty("messages")[0].GetProperty("content")[0].GetProperty("input_audio").GetProperty("data").GetBytesFromBase64();
        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
    }

    [Fact]
    public async Task TranscribeAsync_NonSuccessStatus_DoesNotExposeResponseBody()
    {
        var handler = new DelegateHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("private server diagnostic containing audio metadata")
        }));
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://127.0.0.1:8080/")
        };
        var client = new LlamaCppChatAsrClient(httpClient, "qwen3-asr", "Spanish", "test-secret", () => true);

        var response = await client.TranscribeAsync("work-2", [1, 2], CancellationToken.None);

        Assert.False(response.Success);
        Assert.Contains("400", response.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("private server diagnostic", response.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TranscribeAsync_SendsBearerOnlyAfterListenerOwnershipCheck()
    {
        var calls = 0;
        var handler = new DelegateHandler((request, _) =>
        {
            calls++;
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-secret", request.Headers.Authorization?.Parameter);
            return Task.FromResult(JsonResponse("texto"));
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:8080/") };
        var denied = new LlamaCppChatAsrClient(httpClient, "qwen3-asr", "Spanish", "test-secret", () => false);

        var failure = await denied.TranscribeAsync("denied", new byte[32_000], CancellationToken.None);
        Assert.False(failure.Success);
        Assert.Equal(0, calls);

        var allowed = new LlamaCppChatAsrClient(httpClient, "qwen3-asr", "Spanish", "test-secret", () => true);
        Assert.True((await allowed.TranscribeAsync("allowed", new byte[32_000], CancellationToken.None)).Success);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task TranscribeAsync_RejectsOversizeAudioAndResponse()
    {
        var calls = 0;
        var handler = new DelegateHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(new string('x', 70_000))
            });
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:8080/") };
        var client = new LlamaCppChatAsrClient(httpClient, "qwen3-asr", "Spanish", "test-secret", () => true);

        Assert.False((await client.TranscribeAsync("audio", new byte[16_000 * 2 * 61], CancellationToken.None)).Success);
        Assert.Equal(0, calls);
        var response = await client.TranscribeAsync("reply", new byte[32_000], CancellationToken.None);
        Assert.False(response.Success);
        Assert.Equal(1, calls);
        Assert.DoesNotContain(new string('x', 100), response.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TranscribeAsync_InvalidJsonAndTimeoutReturnSanitizedFailures()
    {
        using var invalidClient = new HttpClient(new DelegateHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"choices\":[]}") })))
        { BaseAddress = new Uri("http://127.0.0.1:8080/") };
        var invalid = new LlamaCppChatAsrClient(invalidClient, "qwen3-asr", "Spanish", "test-secret", () => true);
        Assert.False((await invalid.TranscribeAsync("json", new byte[32_000], CancellationToken.None)).Success);

        using var timeoutClient = new HttpClient(new DelegateHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return JsonResponse("never");
        })) { BaseAddress = new Uri("http://127.0.0.1:8080/") };
        var timed = new LlamaCppChatAsrClient(timeoutClient, "qwen3-asr", "Spanish",
            "test-secret", () => true, TimeSpan.FromMilliseconds(30));
        var response = await timed.TranscribeAsync("timeout", new byte[32_000], CancellationToken.None);
        Assert.False(response.Success);
        Assert.Contains("tiempo", response.Error, StringComparison.OrdinalIgnoreCase);
        using var userCancellation = new CancellationTokenSource();
        userCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            timed.TranscribeAsync("cancelled", new byte[32_000], userCancellation.Token));
    }

    [Fact]
    public async Task WindowsTcpListenerOwner_RequiresExactPortAndProcess()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var owned = false;
        for (var attempt = 0; attempt < 20 && !owned; attempt++)
        {
            owned = WindowsTcpListenerOwner.IsOwnedBy(port, Environment.ProcessId);
            if (!owned) await Task.Delay(25);
        }

        Assert.True(owned);
        Assert.False(WindowsTcpListenerOwner.IsOwnedBy(port, Environment.ProcessId + 1));
        Assert.False(WindowsTcpListenerOwner.IsOwnedBy(port + 1, Environment.ProcessId));
    }

    [Fact]
    public void WindowsTcpListenerOwner_RejectsForeignOrAmbiguousRows()
    {
        var loopback = BitConverter.ToUInt32(IPAddress.Loopback.GetAddressBytes());
        var own = new WindowsTcpOwnershipRow(2, loopback, 8080, 0, 0, 123);
        var foreign = own with { ProcessId = 456 };
        var wildcard = foreign with { LocalAddress = 0 };

        Assert.True(WindowsTcpListenerOwner.HasUniqueListenerOwner([own], 8080, 123));
        Assert.False(WindowsTcpListenerOwner.HasUniqueListenerOwner([own, foreign], 8080, 123));
        Assert.False(WindowsTcpListenerOwner.HasUniqueListenerOwner([own, wildcard], 8080, 123));

        var established = new WindowsTcpOwnershipRow(5, loopback, 8080, loopback, 50000, 123);
        Assert.True(WindowsTcpListenerOwner.HasUniqueEstablishedServerOwner(
            [established], 8080, 50000, 123));
        Assert.False(WindowsTcpListenerOwner.HasUniqueEstablishedServerOwner(
            [established with { ProcessId = 456 }], 8080, 50000, 123));
        Assert.False(WindowsTcpListenerOwner.HasUniqueEstablishedServerOwner(
            [established, established with { ProcessId = 456 }], 8080, 50000, 123));
    }

    [Fact]
    public async Task WindowsTcpListenerOwner_IdentifiesAcceptedConnectionOwner()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var serverPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var accept = listener.AcceptSocketAsync();
        await client.ConnectAsync(IPAddress.Loopback, serverPort);
        using var accepted = await accept;
        var clientPort = ((IPEndPoint)client.LocalEndPoint!).Port;
        var owned = false;
        for (var attempt = 0; attempt < 20 && !owned; attempt++)
        {
            owned = WindowsTcpListenerOwner.IsEstablishedConnectionOwnedBy(
                serverPort, clientPort, Environment.ProcessId);
            if (!owned) await Task.Delay(25);
        }

        Assert.True(owned);
        Assert.False(WindowsTcpListenerOwner.IsEstablishedConnectionOwnedBy(
            serverPort, clientPort, Environment.ProcessId + 1));
    }

    [Fact]
    public async Task OpenModelReadLock_BlocksNormalWritesUntilDisposed()
    {
        var path = Path.Combine(Path.GetTempPath(), "trazio-model-lock-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        try
        {
            await using (var locked = LlamaCppAsrTransportFactory.OpenModelReadLock(path))
            {
                Assert.Throws<IOException>(() =>
                {
                    using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                });
                Assert.Throws<IOException>(() => File.Delete(path));
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("language Spanish<asr_text> texto ", "texto")]
    [InlineData("texto directo<|endoftext|>", "texto directo")]
    [InlineData("   ", "")]
    public void NormalizeOutput_RemovesOnlyQwenControlEnvelope(string input, string expected)
    {
        Assert.Equal(expected, LlamaCppChatAsrClient.NormalizeOutput(input));
    }

    private static HttpResponseMessage JsonResponse(string content)
    {
        var payload = JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content } } }
        });
        return new(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
    }

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }
}
