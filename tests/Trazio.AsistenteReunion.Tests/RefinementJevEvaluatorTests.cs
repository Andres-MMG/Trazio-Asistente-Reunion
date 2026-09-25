using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Trazio.AsistenteReunion.App;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.Tests;

public sealed class RefinementJevEvaluatorTests
{
    private static readonly RefinementEvaluationSnapshot Snapshot = new(
        "batch", "session", AudioSourceKind.Microphone, TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8), "No fueron tres pesos, fueron trece.",
        [new("proposal-1", "No fueron tres pesos; fueron trece.")]);
    private static readonly TranscriptContextPacket Context = new(
        [new(TimeSpan.Zero, TimeSpan.FromSeconds(2), "Antecedente aprobado", true)], [], ["Trazio"]);
    private static readonly JevFallbackSettings Settings = JevFallbackSettingsPolicy.Create("separate-private-key");

    [Fact]
    public async Task EvaluateAsync_ExactAuthorizedBytesGoToFixedEndpointAndNoAudio()
    {
        string? preview = null;
        var calls = 0;
        using var evaluator = new RefinementJevEvaluator(new DelegateHandler(async (request, token) =>
        {
            calls++;
            Assert.Equal(RefinementJevEvaluator.Endpoint, request.RequestUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal(Settings.ApiKey, request.Headers.Authorization?.Parameter);
            var actual = await request.Content!.ReadAsByteArrayAsync(token);
            Assert.Equal(Encoding.UTF8.GetBytes(preview!), actual);
            Assert.DoesNotContain("\"pcm\"", preview!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\"wav\"", preview!, StringComparison.OrdinalIgnoreCase);
            return Response(ValidResponse("proposal-1"));
        }));
        var result = await evaluator.EvaluateAsync(Snapshot, Context, Settings,
            RefinementJevFallbackReason.NotInstalled,
            (outbound, _) =>
            {
                Assert.Equal(RefinementJevEvaluator.Endpoint, outbound.Endpoint);
                Assert.Equal(RefinementJevEvaluator.Model, outbound.Model);
                Assert.Equal(RefinementJevFallbackReason.NotInstalled, outbound.Reason);
                preview = outbound.RequestBody;
                Assert.DoesNotContain(Settings.ApiKey, preview, StringComparison.Ordinal);
                return Task.FromResult(true);
            }, () => true, CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.Equal("jev", result.Evaluator.Model);
        Assert.Equal("jev-1.13.0", result.Evaluator.ModelVersion);
        Assert.Equal(RefinementJevFallbackReason.NotInstalled, result.Evaluator.JevFallbackReason);
        Assert.Equal("proposal-1", result.Judgment.ChoiceId);
        using var document = JsonDocument.Parse(preview!);
        Assert.Equal("jev-latest", document.RootElement.GetProperty("model").GetString());
        Assert.Equal(JsonValueKind.Object, document.RootElement.GetProperty("state").ValueKind);
        Assert.Contains(Snapshot.OriginalText, preview, StringComparison.Ordinal);
        Assert.Equal(3, document.RootElement.GetProperty("questions").EnumerateObject().Count());
        Assert.Equal(RefinementEvaluationRecommendation.HumanReview,
            TranscriptRefinementEvaluationPolicy.Decide(Snapshot, result.Judgment).Recommendation);
    }

    [Fact]
    public async Task EvaluateAsync_NoConsentOrStaleSelectionNeverSends()
    {
        var calls = 0;
        using var evaluator = new RefinementJevEvaluator(new DelegateHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(Response(ValidResponse("proposal-1")));
        }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            evaluator.EvaluateAsync(Snapshot, Context, Settings, RefinementJevFallbackReason.Unavailable,
                (_, _) => Task.FromResult(false), () => true, CancellationToken.None));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            evaluator.EvaluateAsync(Snapshot, Context, Settings, RefinementJevFallbackReason.Unavailable,
                (_, _) => Task.FromResult(true), () => false, CancellationToken.None));
        var current = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            evaluator.EvaluateAsync(Snapshot, Context, Settings, RefinementJevFallbackReason.Unavailable,
                (_, _) => { current = false; return Task.FromResult(true); },
                () => current, CancellationToken.None));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task EvaluateAsync_ZeroProposalsOrCancelledTokenFailsBeforeConsentAndNetwork()
    {
        var consent = 0;
        var calls = 0;
        using var evaluator = new RefinementJevEvaluator(new DelegateHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(Response(ValidResponse("proposal-1")));
        }));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            evaluator.EvaluateAsync(Snapshot with { Proposals = [] }, Context, Settings,
                RefinementJevFallbackReason.NotInstalled,
                (_, _) => { consent++; return Task.FromResult(true); }, () => true, CancellationToken.None));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            evaluator.EvaluateAsync(Snapshot, Context, Settings, RefinementJevFallbackReason.NotInstalled,
                (_, _) => { consent++; return Task.FromResult(true); }, () => true, cancelled.Token));
        Assert.Equal(0, consent);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task EvaluateAsync_TwoProposalsAsksFiveQuestionsAndKeepsOriginal()
    {
        var two = Snapshot with { Proposals = [.. Snapshot.Proposals, new("proposal-2", "Fueron tres.") ] };
        var calls = 0;
        using var evaluator = new RefinementJevEvaluator(new DelegateHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(Response(ValidResponse("proposal-2", "proposal-1")));
        }));
        var result = await evaluator.EvaluateAsync(two, Context, Settings,
            RefinementJevFallbackReason.Capacity,
            (outbound, _) =>
            {
                using var json = JsonDocument.Parse(outbound.RequestBody);
                Assert.Equal(5, json.RootElement.GetProperty("questions").EnumerateObject().Count());
                return Task.FromResult(true);
            }, () => true, CancellationToken.None);
        Assert.Equal(1, calls);
        Assert.Equal(2, result.Judgment.CandidateNouls.Count);
        Assert.Equal(RefinementJevFallbackReason.Capacity, result.Evaluator.JevFallbackReason);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"model\":\"wrong\",\"answers\":{}}")]
    [InlineData("{\"model\":\"jev-1.13.0\",\"answers\":{\"preference\":{\"type\":\"choice\",\"choice\":\"proposal-1\",\"confidence\":1,\"probabilities\":{}}}}")]
    public async Task EvaluateAsync_InvalidResponseIsSanitizedAndNeverRetried(string body)
    {
        var calls = 0;
        using var evaluator = new RefinementJevEvaluator(new DelegateHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(Response(body));
        }));
        var error = await Assert.ThrowsAsync<RefinementJevException>(() =>
            evaluator.EvaluateAsync(Snapshot, Context, Settings, RefinementJevFallbackReason.Unavailable,
                (_, _) => Task.FromResult(true), () => true, CancellationToken.None));
        Assert.DoesNotContain(Snapshot.OriginalText, error.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task EvaluateAsync_HttpFailureDoesNotRetry()
    {
        var calls = 0;
        using var evaluator = new RefinementJevEvaluator(new DelegateHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect));
        }));
        await Assert.ThrowsAsync<RefinementJevException>(() =>
            evaluator.EvaluateAsync(Snapshot, Context, Settings, RefinementJevFallbackReason.Unavailable,
                (_, _) => Task.FromResult(true), () => true, CancellationToken.None));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void FallbackGate_RejectsUncertaintyBusyContextOverflowAndInvalidResponse()
    {
        Assert.Equal(RefinementJevFallbackReason.NotInstalled,
            RefinementJevFallbackGate.EligibleReason(null, null));
        using var installed = new DummyInstallation();
        Assert.Null(RefinementJevFallbackGate.EligibleReason(installed.Settings, null));
        Assert.Null(RefinementJevFallbackGate.EligibleReason(installed.Settings, RefinementLayaFailure.Loading));
        Assert.Null(RefinementJevFallbackGate.EligibleReason(installed.Settings, RefinementLayaFailure.Busy));
        Assert.Null(RefinementJevFallbackGate.EligibleReason(installed.Settings, RefinementLayaFailure.ContextTooLarge));
        Assert.Null(RefinementJevFallbackGate.EligibleReason(installed.Settings, RefinementLayaFailure.InvalidResponse));
        Assert.Equal(RefinementJevFallbackReason.Unavailable,
            RefinementJevFallbackGate.EligibleReason(installed.Settings, RefinementLayaFailure.Unavailable));
        Assert.Equal(RefinementJevFallbackReason.Capacity,
            RefinementJevFallbackGate.EligibleReason(installed.Settings, RefinementLayaFailure.Capacity));
    }

    [Fact]
    public async Task Preflight_StaleUnavailableFailureThenLayaRecovered_DoesNotCallRemote()
    {
        using var installed = new DummyInstallation();
        Assert.Equal(RefinementJevFallbackReason.Unavailable,
            RefinementJevFallbackGate.EligibleReason(installed.Settings, RefinementLayaFailure.Unavailable));
        var localCalls = 0;
        var remoteCalls = 0;
        var result = await RefinementJevFallbackPreflight.RunAsync(
            installed.Settings,
            _ => { localCalls++; return Task.CompletedTask; },
            (_, _) => { remoteCalls++; return Task.FromResult("sent"); },
            CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(1, localCalls);
        Assert.Equal(0, remoteCalls);
    }

    [Theory]
    [InlineData("Busy")]
    [InlineData("Loading")]
    [InlineData("ContextTooLarge")]
    [InlineData("InvalidResponse")]
    public async Task Preflight_NonCapacityLocalFailureNeverCallsRemote(string failure)
    {
        using var installed = new DummyInstallation();
        var remoteCalls = 0;
        await Assert.ThrowsAsync<RefinementLayaException>(() =>
            RefinementJevFallbackPreflight.RunAsync(
                installed.Settings,
                _ => throw new RefinementLayaException(Enum.Parse<RefinementLayaFailure>(failure), "local failure"),
                (_, _) => { remoteCalls++; return Task.FromResult("sent"); },
                CancellationToken.None));
        Assert.Equal(0, remoteCalls);
    }

    [Theory]
    [InlineData("Unavailable", RefinementJevFallbackReason.Unavailable)]
    [InlineData("Capacity", RefinementJevFallbackReason.Capacity)]
    public async Task Preflight_FreshTypedFailurePermitsOneRemoteCall(
        string failure, RefinementJevFallbackReason expected)
    {
        using var installed = new DummyInstallation();
        var remoteCalls = 0;
        var result = await RefinementJevFallbackPreflight.RunAsync(
            installed.Settings,
            _ => throw new RefinementLayaException(Enum.Parse<RefinementLayaFailure>(failure), "fresh local failure"),
            (reason, _) =>
            {
                Assert.Equal(expected, reason);
                remoteCalls++;
                return Task.FromResult("sent");
            }, CancellationToken.None);
        Assert.Equal("sent", result);
        Assert.Equal(1, remoteCalls);
    }

    [Fact]
    public async Task Preflight_DeclinedLocalTrustCannotEnableRemote()
    {
        using var installed = new DummyInstallation();
        var remoteCalls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RefinementJevFallbackPreflight.RunAsync(
                installed.Settings,
                token => throw new OperationCanceledException("Local execution declined.", token),
                (_, _) => { remoteCalls++; return Task.FromResult("sent"); },
                CancellationToken.None));
        Assert.Equal(0, remoteCalls);
    }
    private static string ValidResponse(string selected, string? other = null)
    {
        var probabilities = other is null
            ? new Dictionary<string, double>
            {
                [selected] = 0.84,
                [TranscriptRefinementEvaluationPolicy.KeepOriginalChoice] = 0.10,
                [TranscriptRefinementEvaluationPolicy.HumanReviewChoice] = 0.06
            }
            : new Dictionary<string, double>
            {
                [selected] = 0.81,
                [other] = 0.09,
                [TranscriptRefinementEvaluationPolicy.KeepOriginalChoice] = 0.06,
                [TranscriptRefinementEvaluationPolicy.HumanReviewChoice] = 0.04
            };
        var answers = new Dictionary<string, object>
        {
            ["preference"] = new { type = "choice", choice = selected, confidence = 0.89, probabilities },
            ["semantic_0"] = new { type = "noul", noul = 0.95 },
            ["unsupported_0"] = new { type = "noul", noul = 0.40 }
        };
        if (other is not null)
        {
            answers["semantic_1"] = new { type = "noul", noul = 0.55 };
            answers["unsupported_1"] = new { type = "noul", noul = 0.33 };
        }
        return JsonSerializer.Serialize(new { model = "jev-1.13.0", answers,
            usage = new { input_tokens = 100, output_tokens = 25 } });
    }

    private static HttpResponseMessage Response(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            callback(request, token);
    }

    private sealed class DummyInstallation : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "trazio-jev-" + Guid.NewGuid().ToString("N"));
        public LocalLayaSettings Settings { get; }
        public DummyInstallation()
        {
            var sidecar = Path.Combine(_root, "sidecar");
            var model = Path.Combine(_root, "model");
            Directory.CreateDirectory(Path.Combine(sidecar, "node_modules", "@receptron", "laya"));
            Directory.CreateDirectory(Path.Combine(sidecar, "node_modules", "@huggingface", "tokenizers"));
            Directory.CreateDirectory(Path.Combine(model, "tokenizer"));
            var node = Path.Combine(_root, "node.exe");
            foreach (var file in new[]
            {
                node, Path.Combine(sidecar, "server.mjs"), Path.Combine(sidecar, "package.json"),
                Path.Combine(sidecar, "package-lock.json"), Path.Combine(model, "laya.onnx"),
                Path.Combine(model, "laya.onnx.data"), Path.Combine(model, "laya_config.json"),
                Path.Combine(model, "tokenizer", "tokenizer.json"),
                Path.Combine(model, "tokenizer", "tokenizer_config.json")
            }) File.WriteAllText(file, "test");
            Settings = new(node, sidecar, model, "test");
        }
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
