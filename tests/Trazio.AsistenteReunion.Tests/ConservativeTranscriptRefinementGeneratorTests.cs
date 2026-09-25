using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Trazio.AsistenteReunion.App;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.Tests;

public sealed class ConservativeTranscriptRefinementGeneratorTests
{
    private static readonly TranscriptSegment Segment = new("segment", "session", AudioSourceKind.Microphone, 0,
        TimeSpan.Zero, TimeSpan.FromSeconds(4), "Eh, tenemos dos, no, tres equipos.", DateTimeOffset.UtcNow);
    private static readonly TranscriptContextPacket Context = new(
        [new(TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(1), "Contexto aprobado", true)],
        [new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(6), "Contexto futuro", false)],
        ["Trazio"]);
    private static readonly ExternalAiProviderSettings Remote = ExternalAiProviderPolicy.Create(
        "https://api.example.test/v1/chat/completions", "meeting-editor", "secret-key");
    private static readonly ExternalAiProviderSettings Loopback = ExternalAiProviderPolicy.Create(
        "http://127.0.0.1:8080/v1/chat/completions", "local-editor", null);

    [Fact]
    public async Task GenerateAsync_DeniedRemoteConsent_SendsNothing()
    {
        var calls = 0;
        using var generator = new ConservativeTranscriptRefinementGenerator(new DelegateHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(Response("""{"proposals":[]}"""));
        }));
        RefinementOutboundPreview? preview = null;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => generator.GenerateAsync(
            Segment, Context, Remote, (item, _) => { preview = item; return Task.FromResult(false); }, CancellationToken.None));

        Assert.Equal(0, calls);
        Assert.Equal(Remote.Endpoint, preview!.Endpoint);
        Assert.Equal(Remote.Model, preview.Model);
        Assert.Contains(Segment.Text, preview.RequestBody, StringComparison.Ordinal);
        Assert.DoesNotContain(Remote.ApiKey!, preview.RequestBody, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[]", 0)]
    [InlineData("[{\"text\":\"Tenemos tres equipos.\",\"ambiguousAlternative\":false}]", 1)]
    [InlineData("[{\"text\":\"Tenemos tres equipos.\",\"ambiguousAlternative\":false},{\"text\":\"Tenemos tres equipos disponibles.\",\"ambiguousAlternative\":true}]", 2)]
    public async Task GenerateAsync_ValidProviderResponse_ReturnsBoundedProposals(string proposals, int count)
    {
        var sent = 0;
        string? body = null;
        var handler = new DelegateHandler(async (request, token) =>
        {
            sent++;
            body = await request.Content!.ReadAsStringAsync(token);
            Assert.Equal(Remote.Endpoint, request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal(Remote.ApiKey, request.Headers.Authorization.Parameter);
            return Response("{\"proposals\":" + proposals + "}");
        });
        using var generator = new ConservativeTranscriptRefinementGenerator(handler);
        RefinementOutboundPreview? preview = null;

        var drafts = await generator.GenerateAsync(Segment, Context, Remote,
            (item, _) => { preview = item; return Task.FromResult(true); }, CancellationToken.None);

        Assert.Equal(count, drafts.Count);
        Assert.Equal(1, sent);
        Assert.Equal(body, preview!.RequestBody);
        using var parsed = JsonDocument.Parse(body!);
        var root = parsed.RootElement;
        Assert.Equal(Remote.Model, root.GetProperty("model").GetString());
        Assert.False(root.GetProperty("stream").GetBoolean());
        var user = root.GetProperty("messages")[1].GetProperty("content");
        Assert.Equal(Segment.Text, user.GetProperty("original").GetString());
        Assert.Equal("Contexto aprobado", user.GetProperty("previous")[0].GetProperty("text").GetString());
        Assert.Equal("Trazio", user.GetProperty("glossary")[0].GetString());
    }

    [Fact]
    public async Task GenerateAsync_Loopback_DoesNotRequireOutboundAuthorization()
    {
        var callbacks = 0;
        var sends = 0;
        using var generator = new ConservativeTranscriptRefinementGenerator(new DelegateHandler((_, _) =>
        {
            sends++;
            return Task.FromResult(Response("""{"proposals":[]}"""));
        }));

        await generator.GenerateAsync(Segment, new([], [], []), Loopback,
            (_, _) => { callbacks++; return Task.FromResult(false); }, CancellationToken.None);

        Assert.Equal(0, callbacks);
        Assert.Equal(1, sends);
    }

    [Theory]
    [InlineData("{\"proposals\":[{\"text\":\"eh, tenemos dos, no, tres equipos.\",\"ambiguousAlternative\":false}]}")]
    [InlineData("{\"proposals\":[{\"text\":\"Primera\",\"ambiguousAlternative\":false},{\"text\":\" PRIMERA \",\"ambiguousAlternative\":true}]}")]
    [InlineData("{\"proposals\":[{\"text\":\"Primera\",\"ambiguousAlternative\":false},{\"text\":\"Segunda\",\"ambiguousAlternative\":false}]}")]
    public async Task GenerateAsync_DuplicateOrInvalidProposal_FailsClosed(string content)
    {
        using var generator = new ConservativeTranscriptRefinementGenerator(new DelegateHandler((_, _) =>
            Task.FromResult(Response(content))));

        await Assert.ThrowsAsync<InvalidDataException>(() => generator.GenerateAsync(
            Segment, new([], [], []), Loopback, (_, _) => Task.FromResult(true), CancellationToken.None));
    }

    [Fact]
    public async Task GenerateAsync_OversizedResponseWithoutContentLength_FailsClosed()
    {
        var content = new string('x', 17_000);
        using var generator = new ConservativeTranscriptRefinementGenerator(new DelegateHandler((_, _) =>
        {
            var response = Response(content);
            response.Content.Headers.ContentLength = null;
            return Task.FromResult(response);
        }));

        await Assert.ThrowsAsync<InvalidDataException>(() => generator.GenerateAsync(
            Segment, new([], [], []), Loopback, (_, _) => Task.FromResult(true), CancellationToken.None));
    }

    [Fact]
    public async Task GenerateAsync_CancelledBeforeConsent_SendsNothing()
    {
        var sent = 0;
        using var generator = new ConservativeTranscriptRefinementGenerator(new DelegateHandler((_, _) =>
        {
            sent++;
            return Task.FromResult(Response("""{"proposals":[]}"""));
        }));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => generator.GenerateAsync(
            Segment, new([], [], []), Remote, (_, _) => Task.FromResult(true), cancellation.Token));
        Assert.Equal(0, sent);
    }

    [Fact]
    public async Task GenerateAsync_RemoteRedirect_FailsWithoutFollowing()
    {
        var sent = 0;
        using var generator = new ConservativeTranscriptRefinementGenerator(new DelegateHandler((_, _) =>
        {
            sent++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                Headers = { Location = new Uri("https://other.example.test/collect") }
            });
        }));

        await Assert.ThrowsAsync<InvalidOperationException>(() => generator.GenerateAsync(
            Segment, new([], [], []), Remote, (_, _) => Task.FromResult(true), CancellationToken.None));
        Assert.Equal(1, sent);
    }

    [Fact]
    public async Task GenerateAsync_OversizedContext_FailsBeforeConsentOrNetwork()
    {
        var callbacks = 0;
        var sends = 0;
        using var generator = new ConservativeTranscriptRefinementGenerator(new DelegateHandler((_, _) =>
        {
            sends++;
            return Task.FromResult(Response("""{"proposals":[]}"""));
        }));
        var context = new TranscriptContextPacket(
            [new(TimeSpan.Zero, TimeSpan.FromSeconds(1), new string('x', 4_001), true)], [], []);

        await Assert.ThrowsAsync<ArgumentException>(() => generator.GenerateAsync(
            Segment, context, Remote, (_, _) => { callbacks++; return Task.FromResult(true); }, CancellationToken.None));
        Assert.Equal(0, callbacks);
        Assert.Equal(0, sends);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"choices\":{}}")]
    [InlineData("{\"choices\":[{}]}")]
    [InlineData("{\"choices\":[{\"finish_reason\":42,\"message\":{\"content\":\"private-response-marker\"}}]}")]
    [InlineData("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":42}}]}")]
    public async Task GenerateAsync_MalformedEnvelope_FailsWithSanitizedDataError(string envelope)
    {
        using var generator = new ConservativeTranscriptRefinementGenerator(new DelegateHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(envelope, Encoding.UTF8, "application/json")
            })));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => generator.GenerateAsync(
            Segment, new([], [], []), Loopback, (_, _) => Task.FromResult(true), CancellationToken.None));

        Assert.Equal("La respuesta de refinamiento no tiene un formato válido.", error.Message);
        Assert.DoesNotContain("private-response-marker", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"proposals\":{}}")]
    [InlineData("{\"proposals\":[42]}")]
    [InlineData("{\"proposals\":[{\"ambiguousAlternative\":false}]}")]
    [InlineData("{\"proposals\":[{\"text\":42,\"ambiguousAlternative\":false}]}")]
    [InlineData("{\"proposals\":[{\"text\":\"private-response-marker\",\"ambiguousAlternative\":\"false\"}]}")]
    public async Task GenerateAsync_MalformedProposal_FailsWithSanitizedDataError(string content)
    {
        using var generator = new ConservativeTranscriptRefinementGenerator(new DelegateHandler((_, _) =>
            Task.FromResult(Response(content))));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => generator.GenerateAsync(
            Segment, new([], [], []), Loopback, (_, _) => Task.FromResult(true), CancellationToken.None));

        Assert.Equal("La respuesta de refinamiento no tiene un formato válido.", error.Message);
        Assert.DoesNotContain("private-response-marker", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadBoundedResponseAsync_ZeroesScratchBufferOnSuccess()
    {
        var scratch = Enumerable.Repeat((byte)0x7a, 64).ToArray();
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("sensitive-response"));

        var result = await ConservativeTranscriptRefinementGenerator.ReadBoundedResponseAsync(
            stream, scratch, 64, CancellationToken.None);

        Assert.Equal("sensitive-response", Encoding.UTF8.GetString(result));
        Assert.All(scratch, value => Assert.Equal((byte)0, value));
        CryptographicOperations.ZeroMemory(result);
    }

    [Fact]
    public async Task ReadBoundedResponseAsync_ZeroesScratchBufferOnOversizeFailure()
    {
        var scratch = Enumerable.Repeat((byte)0x7a, 8).ToArray();
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("sensitive-response"));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ConservativeTranscriptRefinementGenerator.ReadBoundedResponseAsync(
                stream, scratch, 4, CancellationToken.None));

        Assert.All(scratch, value => Assert.Equal((byte)0, value));
    }
    [Fact]
    public async Task ReadBoundedResponseAsync_ZeroesScratchBufferOnCancellation()
    {
        var scratch = Enumerable.Repeat((byte)0x7a, 8).ToArray();
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("sensitive-response"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ConservativeTranscriptRefinementGenerator.ReadBoundedResponseAsync(
                stream, scratch, 64, cancellation.Token));

        Assert.All(scratch, value => Assert.Equal((byte)0, value));
    }
    private static HttpResponseMessage Response(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":" +
            JsonSerializer.Serialize(content) + "}}]}", Encoding.UTF8, "application/json")
    };

    private sealed class DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            callback(request, cancellationToken);
    }
}
