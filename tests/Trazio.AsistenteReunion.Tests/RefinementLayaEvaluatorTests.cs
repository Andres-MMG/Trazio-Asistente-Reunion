using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Trazio.AsistenteReunion.App;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.Tests;

public sealed class RefinementLayaEvaluatorTests
{
    private static readonly RefinementEvaluationSnapshot Snapshot = new(
        "batch", "session", AudioSourceKind.SystemOutput, TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8), "Texto original, no dos.",
        [new("proposal", "Texto corregido, no tres.")]);
    private static readonly TranscriptContextPacket Context = new(
        [new(TimeSpan.Zero, TimeSpan.FromSeconds(2), "Antecedente revisado", true)], [], ["Trazio"]);
    private static readonly RefinementLayaSettings Settings = RefinementLayaSettings.Create(
        "http://127.0.0.1:48731/v1/system-one", new string('t', 32), "laya-multilingual-v1");

    [Fact]
    public async Task EvaluateAsync_UsesOnlyLiteralLoopbackAndSendsTextOnlyInState()
    {
        string? payload = null;
        using var evaluator = new RefinementLayaEvaluator(new DelegateHandler(async (request, token) =>
        {
            Assert.Equal("127.0.0.1", request.RequestUri!.Host);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal(Settings.Token, request.Headers.Authorization.Parameter);
            payload = await request.Content!.ReadAsStringAsync(token);
            return Response(ValidResponse());
        }));

        var result = await evaluator.EvaluateAsync(Snapshot, Context, Settings, CancellationToken.None);

        Assert.Equal("laya", result.Evaluator.Model);
        Assert.Equal(Settings.ModelVersion, result.Evaluator.ModelVersion);
        Assert.Equal("proposal", result.Judgment.ChoiceId);
        Assert.Equal(0.98, result.Judgment.CandidateNouls[0].SemanticPreservation);
        using var body = JsonDocument.Parse(payload!);
        var root = body.RootElement;
        Assert.Equal(JsonValueKind.String, root.GetProperty("state").ValueKind);
        Assert.Contains(Snapshot.OriginalText, root.GetProperty("state").GetString(), StringComparison.Ordinal);
        Assert.Contains(Snapshot.Proposals[0].Text, root.GetProperty("state").GetString(), StringComparison.Ordinal);
        var questions = root.GetProperty("questions");
        Assert.Equal(3, questions.EnumerateObject().Count());
        Assert.Equal("choice", questions.GetProperty("preference").GetProperty("type").GetString());
        Assert.Equal("noul", questions.GetProperty("semantic_0").GetProperty("type").GetString());
        Assert.Equal("noul", questions.GetProperty("unsupported_0").GetProperty("type").GetString());
        Assert.DoesNotContain(Snapshot.Proposals[0].Text, questions.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(RefinementEvaluationRecommendation.HumanReview,
            TranscriptRefinementEvaluationPolicy.Decide(Snapshot, result.Judgment).Recommendation);
    }

    [Theory]
    [InlineData("http://localhost:48731/v1/system-one")]
    [InlineData("https://127.0.0.1:48731/v1/system-one")]
    [InlineData("http://192.168.1.2:48731/v1/system-one")]
    [InlineData("http://127.0.0.1:48731/other")]
    public void Settings_RejectsNonLiteralLoopbackOrWrongRoute(string endpoint)
    {
        Assert.Throws<ArgumentException>(() => RefinementLayaSettings.Create(endpoint, new string('t', 32), "v1"));
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, "Loading")]
    [InlineData(HttpStatusCode.InternalServerError, "InvalidResponse")]
    [InlineData(HttpStatusCode.InsufficientStorage, "Capacity")]
    [InlineData(HttpStatusCode.TooManyRequests, "Busy")]
    [InlineData(HttpStatusCode.UnprocessableEntity, "ContextTooLarge")]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, "ContextTooLarge")]
    [InlineData(HttpStatusCode.Redirect, "InvalidResponse")]
    public async Task EvaluateAsync_FailureStatusIsTypedAndDoesNotRetry(HttpStatusCode status, string expected)
    {
        var calls = 0;
        using var evaluator = new RefinementLayaEvaluator(new DelegateHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(status));
        }));

        var error = await Assert.ThrowsAsync<RefinementLayaException>(() =>
            evaluator.EvaluateAsync(Snapshot, Context, Settings, CancellationToken.None));

        Assert.Equal(expected, error.Failure.ToString());
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"model\":\"laya\",\"model_version\":\"wrong\",\"answers\":{}}")]
    [InlineData("{\"model\":\"laya\",\"model_version\":\"laya-multilingual-v1\",\"answers\":{\"preference\":{\"type\":42}}}")]
    public async Task EvaluateAsync_InvalidResponseFailsClosedWithoutLeakingContent(string body)
    {
        using var evaluator = new RefinementLayaEvaluator(new DelegateHandler((_, _) => Task.FromResult(Response(body))));

        var error = await Assert.ThrowsAsync<RefinementLayaException>(() =>
            evaluator.EvaluateAsync(Snapshot, Context, Settings, CancellationToken.None));

        Assert.Equal(RefinementLayaFailure.InvalidResponse, error.Failure);
        Assert.DoesNotContain("wrong", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvaluateAsync_OversizedResponseAndCancellationDoNotRetry()
    {
        var sends = 0;
        using var evaluator = new RefinementLayaEvaluator(new DelegateHandler((_, _) =>
        {
            sends++;
            var response = Response(new string('x', 17_000));
            response.Content.Headers.ContentLength = null;
            return Task.FromResult(response);
        }));
        var oversized = await Assert.ThrowsAsync<RefinementLayaException>(() =>
            evaluator.EvaluateAsync(Snapshot, Context, Settings, CancellationToken.None));
        Assert.Equal(RefinementLayaFailure.InvalidResponse, oversized.Failure);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            evaluator.EvaluateAsync(Snapshot, Context, Settings, cancellation.Token));
        Assert.Equal(1, sends);
    }

    [Fact]
    public async Task EvaluateAsync_ZeroProposalsFailsBeforeNetwork()
    {
        var sends = 0;
        using var evaluator = new RefinementLayaEvaluator(new DelegateHandler((_, _) =>
        {
            sends++;
            return Task.FromResult(Response(ValidResponse()));
        }));
        await Assert.ThrowsAsync<ArgumentException>(() => evaluator.EvaluateAsync(
            Snapshot with { Proposals = [] }, Context, Settings, CancellationToken.None));
        Assert.Equal(0, sends);
    }

    [Fact]
    public async Task EvaluateAsync_JointQwenWithoutProposalsSendsDistinctAcousticCandidate()
    {
        var qwen = new RefinementObservedAsrCandidate(
            "revision", "segment", TranscriptRefinementEvaluationPolicy.QwenChoiceId("revision"),
            "Hipótesis acústica independiente", "qwen3-asr", "SHA256:" + new string('A', 64) +
            ";MMPROJ-SHA256:" + new string('B', 64), "es", Snapshot.Source,
            Snapshot.Start, Snapshot.End, ModelRevisionProducer.Qwen3AsrLlamaCppV1);
        var joint = Snapshot with { Proposals = [], QwenAsr = qwen, CorrectionRevision = 0 };
        string? payload = null;
        using var evaluator = new RefinementLayaEvaluator(new DelegateHandler(async (request, token) =>
        {
            payload = await request.Content!.ReadAsStringAsync(token);
            return Response(JsonSerializer.Serialize(new
            {
                model = "laya", model_version = Settings.ModelVersion,
                answers = new Dictionary<string, object>
                {
                    ["preference"] = new { type = "choice", choice = qwen.ChoiceId,
                        confidence = 0.98, probabilities = new Dictionary<string, double>
                        {
                            [qwen.ChoiceId] = 0.90,
                            [TranscriptRefinementEvaluationPolicy.KeepOriginalChoice] = 0.06,
                            [TranscriptRefinementEvaluationPolicy.HumanReviewChoice] = 0.04
                        } },
                    ["semantic_0"] = new { type = "noul", noul = 0.99 },
                    ["unsupported_0"] = new { type = "noul", noul = 0.01 }
                }
            }));
        }));

        var result = await evaluator.EvaluateAsync(joint, Context, Settings, CancellationToken.None);

        Assert.Equal(RefinementLayaEvaluator.JointQuestionVersion, result.Evaluator.QuestionVersion);
        Assert.Equal(qwen.ChoiceId, result.Judgment.ChoiceId);
        Assert.Equal(RefinementEvaluationRecommendation.HumanReview,
            TranscriptRefinementEvaluationPolicy.DecideForVersion(
                TranscriptRefinementEvaluationPolicy.Version2, joint, result.Judgment).Recommendation);
        using var body = JsonDocument.Parse(payload!);
        var state = body.RootElement.GetProperty("state").GetString()!;
        Assert.Contains("observed_asr", state, StringComparison.Ordinal);
        Assert.Contains(qwen.ModelHash, state, StringComparison.Ordinal);
        Assert.DoesNotContain(qwen.Text, body.RootElement.GetProperty("questions").GetRawText(), StringComparison.Ordinal);
        Assert.Equal(3, body.RootElement.GetProperty("questions").EnumerateObject().Count());
    }
    private static string ValidResponse() => JsonSerializer.Serialize(new
    {
        model = "laya", model_version = Settings.ModelVersion,
        answers = new Dictionary<string, object>
        {
            ["preference"] = new { type = "choice", choice = "proposal", confidence = 0.90,
                probabilities = new Dictionary<string, double>
                {
                    ["proposal"] = 0.84, [TranscriptRefinementEvaluationPolicy.KeepOriginalChoice] = 0.10,
                    [TranscriptRefinementEvaluationPolicy.HumanReviewChoice] = 0.06
                } },
            ["semantic_0"] = new { type = "noul", noul = 0.98 },
            ["unsupported_0"] = new { type = "noul", noul = 0.40 }
        }
    });

    private static HttpResponseMessage Response(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            callback(request, cancellationToken);
    }
}
