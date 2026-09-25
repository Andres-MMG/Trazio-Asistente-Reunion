using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.App;

internal sealed record RefinementJevOutboundPreview(
    Uri Endpoint, string Model, string RequestBody, RefinementJevFallbackReason Reason);

internal static class RefinementJevFallbackGate
{
    public static RefinementJevFallbackReason? EligibleReason(
        LocalLayaSettings? installed, RefinementLayaFailure? lastFailure)
    {
        if (installed is null || ClearlyMissingInstallation(installed))
            return RefinementJevFallbackReason.NotInstalled;
        return lastFailure switch
        {
            RefinementLayaFailure.Capacity => RefinementJevFallbackReason.Capacity,
            RefinementLayaFailure.Unavailable => RefinementJevFallbackReason.Unavailable,
            _ => null
        };
    }

    private static bool ClearlyMissingInstallation(LocalLayaSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.NodeExecutablePath) ||
            string.IsNullOrWhiteSpace(settings.SidecarDirectory) ||
            string.IsNullOrWhiteSpace(settings.ModelDirectory))
            return true;
        if (!File.Exists(settings.NodeExecutablePath) ||
            !Directory.Exists(settings.SidecarDirectory) ||
            !Directory.Exists(settings.ModelDirectory))
            return true;
        return !File.Exists(Path.Combine(settings.SidecarDirectory, "server.mjs")) ||
               !File.Exists(Path.Combine(settings.SidecarDirectory, "package.json")) ||
               !File.Exists(Path.Combine(settings.SidecarDirectory, "package-lock.json")) ||
               !Directory.Exists(Path.Combine(settings.SidecarDirectory, "node_modules", "@receptron", "laya")) ||
               !Directory.Exists(Path.Combine(settings.SidecarDirectory, "node_modules", "@huggingface", "tokenizers")) ||
               !File.Exists(Path.Combine(settings.ModelDirectory, "laya.onnx")) ||
               !File.Exists(Path.Combine(settings.ModelDirectory, "laya.onnx.data")) ||
               !File.Exists(Path.Combine(settings.ModelDirectory, "laya_config.json")) ||
               !File.Exists(Path.Combine(settings.ModelDirectory, "tokenizer", "tokenizer.json")) ||
               !File.Exists(Path.Combine(settings.ModelDirectory, "tokenizer", "tokenizer_config.json"));
    }
}

internal static class RefinementJevFallbackPreflight
{
    public static async Task<T?> RunAsync<T>(
        LocalLayaSettings? configuredLaya,
        Func<CancellationToken, Task> retryLocal,
        Func<RefinementJevFallbackReason, CancellationToken, Task<T>> evaluateRemotely,
        CancellationToken cancellationToken) where T : class
    {
        ArgumentNullException.ThrowIfNull(retryLocal);
        ArgumentNullException.ThrowIfNull(evaluateRemotely);
        cancellationToken.ThrowIfCancellationRequested();
        if (RefinementJevFallbackGate.EligibleReason(configuredLaya, null) ==
            RefinementJevFallbackReason.NotInstalled)
            return await evaluateRemotely(RefinementJevFallbackReason.NotInstalled, cancellationToken);
        try
        {
            await retryLocal(cancellationToken);
            return null;
        }
        catch (RefinementLayaException ex) when (
            ex.Failure is RefinementLayaFailure.Unavailable or RefinementLayaFailure.Capacity)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reason = ex.Failure == RefinementLayaFailure.Capacity
                ? RefinementJevFallbackReason.Capacity
                : RefinementJevFallbackReason.Unavailable;
            return await evaluateRemotely(reason, cancellationToken);
        }
    }
}
internal sealed record RefinementJevEvaluation(
    RefinementEvaluatorIdentity Evaluator, RefinementEvaluationJudgment Judgment);

internal sealed class RefinementJevException(string message) : Exception(message);

internal sealed class RefinementJevEvaluator : IDisposable
{
    public static readonly Uri Endpoint = new("https://api.typesafe.ai/v1/systemone");
    public const string Model = "jev-latest";
    internal const string QuestionVersion = "refinement-jev-questions-v1";
    private const int MaximumRequestBytes = 24 * 1024;
    private const int MaximumResponseBytes = 16 * 1024;
    private readonly HttpClient _client;

    internal RefinementJevEvaluator(HttpMessageHandler? handler = null)
    {
        _client = new HttpClient(handler ?? new HttpClientHandler
        {
            AllowAutoRedirect = false, UseProxy = false, UseCookies = false
        }, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task<RefinementJevEvaluation> EvaluateAsync(
        RefinementEvaluationSnapshot snapshot, TranscriptContextPacket context,
        JevFallbackSettings settings, RefinementJevFallbackReason reason,
        Func<RefinementJevOutboundPreview, CancellationToken, Task<bool>> authorize,
        Func<bool> isCurrent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(authorize);
        ArgumentNullException.ThrowIfNull(isCurrent);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        var key = JevFallbackSettingsPolicy.Create(settings.ApiKey).ApiKey;
        RefinementLayaEvaluator.ValidateInput(snapshot, context);

        var criteria = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [TranscriptRefinementEvaluationPolicy.KeepOriginalChoice] = "El original conserva mejor la información respaldada.",
            [TranscriptRefinementEvaluationPolicy.HumanReviewChoice] = "El texto disponible no permite decidir con seguridad."
        };
        foreach (var proposal in snapshot.Proposals)
            criteria.Add(proposal.ProposalId, "La propuesta conserva mejor el significado del original sin hechos añadidos.");
        var questions = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["preference"] = new
            {
                type = "choice",
                instructions = "Compara solo los textos y el contexto dado; el audio no está disponible. Elige la versión más fiel, o revisión humana si hay duda.",
                criteria
            }
        };
        for (var index = 0; index < snapshot.Proposals.Count; index++)
        {
            var id = snapshot.Proposals[index].ProposalId;
            questions.Add($"semantic_{index}", new
            {
                type = "noul",
                instructions = $"¿La propuesta con id {id} preserva todo el significado del original, incluidas cifras, nombres y negaciones?"
            });
            questions.Add($"unsupported_{index}", new
            {
                type = "noul",
                instructions = $"¿La propuesta con id {id} introduce información no respaldada por el original y el contexto?"
            });
        }
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            state = new
            {
                original = snapshot.OriginalText,
                source = snapshot.Source == AudioSourceKind.Microphone ? "microphone" : "computer_audio",
                proposals = snapshot.Proposals.Select(item => new { id = item.ProposalId, text = item.Text }),
                previous = context.Previous.Select(item => new { text = item.Text, approved = item.HumanApproved }),
                following = context.Following.Select(item => new { text = item.Text, approved = item.HumanApproved }),
                glossary = context.GlossaryTerms
            },
            model = Model,
            questions
        });
        try
        {
            if (payload.Length > MaximumRequestBytes)
                throw new ArgumentException("El contenido de evaluación excede el límite permitido.", nameof(context));
            var preview = new RefinementJevOutboundPreview(
                Endpoint, Model, Encoding.UTF8.GetString(payload), reason);
            if (!isCurrent() || !await authorize(preview, cancellationToken) || !isCurrent())
                throw new OperationCanceledException("El envío a Jev no fue autorizado.", cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Content = new ByteArrayContent(payload);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            HttpResponseMessage response;
            try
            {
                response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            catch (HttpRequestException) { throw new RefinementJevException("Jev no está disponible."); }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            { throw new RefinementJevException("Jev no respondió a tiempo."); }
            using (response)
            {
                if (!response.IsSuccessStatusCode)
                    throw new RefinementJevException("Jev no pudo evaluar este lote.");
                if (response.Content.Headers.ContentLength > MaximumResponseBytes)
                    throw new RefinementJevException("La respuesta de Jev supera el límite permitido.");
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                byte[] bytes;
                try
                {
                    bytes = await ConservativeTranscriptRefinementGenerator.ReadBoundedResponseAsync(
                        stream, new byte[4096], MaximumResponseBytes, cancellationToken);
                }
                catch (InvalidDataException)
                { throw new RefinementJevException("La respuesta de Jev supera el límite permitido."); }
                return ParseResponse(bytes, snapshot, reason);
            }
        }
        finally { CryptographicOperations.ZeroMemory(payload); }
    }

    private static RefinementJevEvaluation ParseResponse(
        byte[] bytes, RefinementEvaluationSnapshot snapshot, RefinementJevFallbackReason reason)
    {
        static RefinementJevException Invalid() => new("Jev devolvió un juicio no válido.");
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("model", out var model) || model.ValueKind != JsonValueKind.String ||
                model.GetString() is not { } version || !version.StartsWith("jev-", StringComparison.Ordinal) ||
                version.Length > 200 || version.Any(char.IsControl) ||
                !root.TryGetProperty("answers", out var answers) || answers.ValueKind != JsonValueKind.Object ||
                answers.EnumerateObject().Count() != 1 + 2 * snapshot.Proposals.Count ||
                !answers.TryGetProperty("preference", out var choice) || choice.ValueKind != JsonValueKind.Object ||
                !choice.TryGetProperty("type", out var choiceType) || choiceType.GetString() != "choice" ||
                !choice.TryGetProperty("choice", out var selected) || selected.ValueKind != JsonValueKind.String ||
                !choice.TryGetProperty("confidence", out var confidence) || confidence.ValueKind != JsonValueKind.Number ||
                !choice.TryGetProperty("probabilities", out var distribution) || distribution.ValueKind != JsonValueKind.Object)
                throw Invalid();
            var probabilities = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var item in distribution.EnumerateObject())
                if (item.Value.ValueKind != JsonValueKind.Number ||
                    !probabilities.TryAdd(item.Name, item.Value.GetDouble()))
                    throw Invalid();
            var nouls = new List<RefinementCandidateNouls>(snapshot.Proposals.Count);
            for (var index = 0; index < snapshot.Proposals.Count; index++)
                nouls.Add(new(snapshot.Proposals[index].ProposalId,
                    ReadNoul(answers, $"semantic_{index}"), ReadNoul(answers, $"unsupported_{index}")));
            var judgment = new RefinementEvaluationJudgment(
                selected.GetString()!, confidence.GetDouble(), probabilities, nouls);
            TranscriptRefinementEvaluationPolicy.Decide(snapshot, judgment);
            if (probabilities[judgment.ChoiceId] < probabilities.Values.Max())
                throw Invalid();
            return new(new("jev", version, QuestionVersion, JevFallbackReason: reason), judgment);
        }
        catch (JsonException) { throw Invalid(); }
        catch (ArgumentException) { throw Invalid(); }
        catch (KeyNotFoundException) { throw Invalid(); }
        catch (InvalidOperationException) { throw Invalid(); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static double ReadNoul(JsonElement answers, string name)
    {
        if (!answers.TryGetProperty(name, out var answer) || answer.ValueKind != JsonValueKind.Object ||
            !answer.TryGetProperty("type", out var type) || type.GetString() != "noul" ||
            !answer.TryGetProperty("noul", out var value) || value.ValueKind != JsonValueKind.Number)
            throw new RefinementJevException("Jev devolvió un juicio no válido.");
        return value.GetDouble();
    }

    public void Dispose() => _client.Dispose();
}
