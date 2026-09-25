using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.App;

internal sealed record RefinementLayaSettings(Uri Endpoint, string Token, string ModelVersion)
{
    public static RefinementLayaSettings Create(string endpoint, string token, string modelVersion)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttp ||
            !IPAddress.TryParse(uri.Host, out var address) || !IPAddress.IsLoopback(address) ||
            uri.AbsolutePath != "/v1/system-one" || uri.Query.Length > 0 || uri.Fragment.Length > 0 ||
            uri.UserInfo.Length > 0)
            throw new ArgumentException("Laya requiere una URL HTTP de loopback literal y la ruta /v1/system-one.", nameof(endpoint));
        if (string.IsNullOrWhiteSpace(token) || token.Length is < 32 or > 256 || token.Any(char.IsWhiteSpace))
            throw new ArgumentException("El token local de Laya no es válido.", nameof(token));
        if (string.IsNullOrWhiteSpace(modelVersion) || modelVersion.Length > 120 || modelVersion.Any(char.IsControl))
            throw new ArgumentException("La versión local de Laya no es válida.", nameof(modelVersion));
        return new(uri, token, modelVersion);
    }
}

internal sealed record RefinementLayaEvaluation(
    RefinementEvaluatorIdentity Evaluator,
    RefinementEvaluationJudgment Judgment);

internal enum RefinementLayaFailure { Loading, Busy, Unavailable, Capacity, ContextTooLarge, InvalidResponse }

internal sealed class RefinementLayaException(RefinementLayaFailure failure, string message)
    : Exception(message)
{
    public RefinementLayaFailure Failure { get; } = failure;
}

internal sealed class RefinementLayaEvaluator : IDisposable
{
    internal const string QuestionVersion = "refinement-laya-questions-v1";
    internal const string JointQuestionVersion = "refinement-laya-questions-v2";
    private const int MaximumRequestBytes = 24 * 1024;
    private const int MaximumResponseBytes = 16 * 1024;
    private readonly HttpClient _client;

    internal RefinementLayaEvaluator(HttpMessageHandler? handler = null)
    {
        _client = new HttpClient(handler ?? new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false
        }, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(60) };
    }

    public async Task<RefinementLayaEvaluation> EvaluateAsync(
        RefinementEvaluationSnapshot snapshot,
        TranscriptContextPacket context,
        RefinementLayaSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();
        if (!settings.Endpoint.IsAbsoluteUri)
            throw new ArgumentException("Laya requiere una URL absoluta.", nameof(settings));
        var validatedSettings = RefinementLayaSettings.Create(
            settings.Endpoint.AbsoluteUri, settings.Token, settings.ModelVersion);
        ValidateInput(snapshot, context);
        var criteria = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [TranscriptRefinementEvaluationPolicy.KeepOriginalChoice] = "El texto original es la opción más segura.",
            [TranscriptRefinementEvaluationPolicy.HumanReviewChoice] = "La evidencia no permite decidir sin una persona."
        };
        var candidates = Candidates(snapshot);
        foreach (var candidate in candidates)
            criteria.Add(candidate.Id, candidate.Kind == "observed_asr"
                ? "Esta transcripción acústica independiente puede reflejar mejor el audio; ninguno de los textos es verdad comprobada."
                : "Esta interpretación generada conserva mejor el significado sin inventar hechos.");
        var questions = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["preference"] = new
            {
                type = "choice",
                instructions = snapshot.QwenAsr is null
                    ? "Según el original y el contexto, elige el texto más fiel. Si hay duda, solicita revisión humana. No infieras del audio: no está disponible."
                    : "Compara dos observaciones ASR y las interpretaciones generadas. Whisper no es verdad acústica; el audio no está disponible. Si hay duda, solicita revisión humana.",
                criteria
            }
        };
        for (var index = 0; index < candidates.Count; index++)
        {
            var candidateId = candidates[index].Id;
            questions.Add($"semantic_{index}", new
            {
                type = "noul",
                instructions = $"¿El candidato con id {candidateId} preserva el significado respaldado por las observaciones y contexto, incluidos nombres, cifras y negaciones? No asumas que Whisper es verdad acústica."
            });
            questions.Add($"unsupported_{index}", new
            {
                type = "noul",
                instructions = $"¿El candidato con id {candidateId} agrega hechos o afirmaciones no sustentados por las observaciones y el contexto?"
            });
        }
        // Laya treats a string state verbatim; the sidecar tokenizes this exact value before inference.
        var state = snapshot.QwenAsr is null
            ? JsonSerializer.Serialize(new
            {
                original = snapshot.OriginalText,
                source = snapshot.Source == AudioSourceKind.Microphone ? "microphone" : "computer_audio",
                proposals = snapshot.Proposals.Select(item => new { id = item.ProposalId, text = item.Text }),
                previous = context.Previous.Select(item => new { text = item.Text, approved = item.HumanApproved }),
                following = context.Following.Select(item => new { text = item.Text, approved = item.HumanApproved }),
                glossary = context.GlossaryTerms
            })
            : JsonSerializer.Serialize(new
            {
                original = new { kind = "observed_asr", model = "whisper", text = snapshot.OriginalText },
                source = snapshot.Source == AudioSourceKind.Microphone ? "microphone" : "computer_audio",
                qwen_asr = new { kind = "observed_asr", id = snapshot.QwenAsr.ChoiceId,
                    model = snapshot.QwenAsr.ModelIdentity, model_hash = snapshot.QwenAsr.ModelHash,
                    producer = snapshot.QwenAsr.ProducerIdentity,
                    start_ticks = snapshot.QwenAsr.Start.Ticks, end_ticks = snapshot.QwenAsr.End.Ticks,
                    text = snapshot.QwenAsr.Text },
                proposals = snapshot.Proposals.Select(item => new
                    { kind = "generated_interpretation", id = item.ProposalId, text = item.Text }),
                previous = context.Previous.Select(item => new { text = item.Text, approved = item.HumanApproved }),
                following = context.Following.Select(item => new { text = item.Text, approved = item.HumanApproved }),
                glossary = context.GlossaryTerms
            });
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { state, questions });
        try
        {
            if (payload.Length > MaximumRequestBytes || Encoding.UTF8.GetByteCount(state) > 16 * 1024)
                throw new RefinementLayaException(RefinementLayaFailure.ContextTooLarge, "La evaluación local supera el tamaño permitido.");
            using var request = new HttpRequestMessage(HttpMethod.Post, validatedSettings.Endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", validatedSettings.Token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Content = new ByteArrayContent(payload);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            HttpResponseMessage response;
            try { response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken); }
            catch (HttpRequestException)
            {
                throw new RefinementLayaException(RefinementLayaFailure.Unavailable, "Laya local no está disponible.");
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new RefinementLayaException(RefinementLayaFailure.Unavailable, "Laya local no respondió a tiempo.");
            }
            using (response)
            {
                if (response.StatusCode is HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout)
                    throw new RefinementLayaException(RefinementLayaFailure.InvalidResponse, "Laya falló durante la evaluación local.");
                if (response.StatusCode == HttpStatusCode.InsufficientStorage)
                    throw new RefinementLayaException(RefinementLayaFailure.Capacity, "El equipo no tiene capacidad suficiente para Laya.");
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    throw new RefinementLayaException(RefinementLayaFailure.Busy, "Laya local está ocupado.");
                if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
                    throw new RefinementLayaException(RefinementLayaFailure.Loading, "Laya local aún no está listo.");
                if (response.StatusCode is HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.UnprocessableEntity)
                    throw new RefinementLayaException(RefinementLayaFailure.ContextTooLarge, "El contexto no cabe completo en Laya.");
                if (!response.IsSuccessStatusCode)
                    throw new RefinementLayaException(RefinementLayaFailure.InvalidResponse, "Laya rechazó la evaluación local.");
                if (response.Content.Headers.ContentLength > MaximumResponseBytes)
                    throw new RefinementLayaException(RefinementLayaFailure.InvalidResponse, "La respuesta local supera el límite permitido.");
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                byte[] bytes;
                try
                {
                    bytes = await ConservativeTranscriptRefinementGenerator.ReadBoundedResponseAsync(
                        stream, new byte[4096], MaximumResponseBytes, cancellationToken);
                }
                catch (InvalidDataException)
                {
                    throw new RefinementLayaException(RefinementLayaFailure.InvalidResponse, "La respuesta local supera el límite permitido.");
                }
                return ParseResponse(bytes, snapshot, validatedSettings.ModelVersion);
            }
        }
        finally { CryptographicOperations.ZeroMemory(payload); }
    }

    private static RefinementLayaEvaluation ParseResponse(
        byte[] bytes, RefinementEvaluationSnapshot snapshot, string expectedVersion)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("model", out var model) || model.ValueKind != JsonValueKind.String || model.GetString() != "laya" ||
                !root.TryGetProperty("model_version", out var version) || version.ValueKind != JsonValueKind.String || version.GetString() != expectedVersion ||
                !root.TryGetProperty("answers", out var answers) || answers.ValueKind != JsonValueKind.Object ||
                answers.EnumerateObject().Count() != 1 + 2 * Candidates(snapshot).Count ||
                !answers.TryGetProperty("preference", out var choice) || choice.ValueKind != JsonValueKind.Object ||
                !choice.TryGetProperty("type", out var choiceType) || choiceType.ValueKind != JsonValueKind.String || choiceType.GetString() != "choice" ||
                !choice.TryGetProperty("choice", out var selected) || selected.ValueKind != JsonValueKind.String ||
                !choice.TryGetProperty("confidence", out var confidence) || confidence.ValueKind != JsonValueKind.Number ||
                !choice.TryGetProperty("probabilities", out var distribution) || distribution.ValueKind != JsonValueKind.Object)
                throw InvalidResponse();
            var probabilities = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var item in distribution.EnumerateObject())
                if (item.Value.ValueKind != JsonValueKind.Number || !probabilities.TryAdd(item.Name, item.Value.GetDouble()))
                    throw InvalidResponse();
            var candidates = Candidates(snapshot);
            var nouls = new List<RefinementCandidateNouls>(candidates.Count);
            for (var index = 0; index < candidates.Count; index++)
            {
                var semantic = ReadNoul(answers, $"semantic_{index}");
                var unsupported = ReadNoul(answers, $"unsupported_{index}");
                nouls.Add(new(candidates[index].Id, semantic, unsupported));
            }
            var judgment = new RefinementEvaluationJudgment(
                selected.GetString()!, confidence.GetDouble(), probabilities, nouls);
            var policyVersion = snapshot.QwenAsr is null
                ? TranscriptRefinementEvaluationPolicy.Version1
                : TranscriptRefinementEvaluationPolicy.Version2;
            TranscriptRefinementEvaluationPolicy.DecideForVersion(policyVersion, snapshot, judgment);
            return new(new("laya", expectedVersion,
                snapshot.QwenAsr is null ? QuestionVersion : JointQuestionVersion), judgment);
        }
        catch (JsonException) { throw InvalidResponse(); }
        catch (ArgumentException) { throw InvalidResponse(); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static double ReadNoul(JsonElement answers, string name)
    {
        if (!answers.TryGetProperty(name, out var answer) || answer.ValueKind != JsonValueKind.Object ||
            !answer.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "noul" ||
            !answer.TryGetProperty("noul", out var value) || value.ValueKind != JsonValueKind.Number)
            throw InvalidResponse();
        return value.GetDouble();
    }

    private static RefinementLayaException InvalidResponse() =>
        new(RefinementLayaFailure.InvalidResponse, "Laya devolvió una evaluación no válida.");

    internal static void ValidateInput(RefinementEvaluationSnapshot snapshot, TranscriptContextPacket context)
    {
        if (string.IsNullOrWhiteSpace(snapshot.BatchId) || string.IsNullOrWhiteSpace(snapshot.SessionId) ||
            snapshot.Proposals is null || snapshot.Proposals.Count > 2 ||
            (snapshot.Proposals.Count == 0 && snapshot.QwenAsr is null) ||
            snapshot.Start < TimeSpan.Zero || snapshot.End <= snapshot.Start ||
            !Enum.IsDefined(snapshot.Source))
            throw new ArgumentException("Se requiere un lote original con una o dos propuestas.", nameof(snapshot));
        TranscriptRefinementPolicy.ValidateText(snapshot.OriginalText, nameof(snapshot));
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var proposal in snapshot.Proposals)
        {
            if (proposal is null || string.IsNullOrWhiteSpace(proposal.ProposalId) ||
                proposal.ProposalId.Length > 64 || !ids.Add(proposal.ProposalId))
                throw new ArgumentException("Las propuestas no son válidas.", nameof(snapshot));
            TranscriptRefinementPolicy.ValidateText(proposal.Text, nameof(snapshot));
        }
        if (snapshot.QwenAsr is not null)
            TranscriptRefinementEvaluationPolicy.ValidateJointSnapshot(snapshot);
        if (context.Previous is null || context.Following is null || context.GlossaryTerms is null ||
            context.Previous.Count > 3 || context.Following.Count > 3 || context.GlossaryTerms.Count > 64 ||
            context.Previous.Concat(context.Following).Any(item => item is null || item.Start < TimeSpan.Zero ||
                item.End <= item.Start || string.IsNullOrWhiteSpace(item.Text) || item.Text.Length > 1000) ||
            context.GlossaryTerms.Any(item => string.IsNullOrWhiteSpace(item) || item.Length > 80) ||
            context.Previous.Sum(item => item.Text.Length) + context.Following.Sum(item => item.Text.Length) +
            context.GlossaryTerms.Sum(item => item.Length) > 4000)
            throw new ArgumentException("El contexto local supera el límite permitido.", nameof(context));
    }

    private static IReadOnlyList<(string Id, string Kind)> Candidates(RefinementEvaluationSnapshot snapshot)
    {
        var result = new List<(string Id, string Kind)>(snapshot.Proposals.Count + (snapshot.QwenAsr is null ? 0 : 1));
        if (snapshot.QwenAsr is not null) result.Add((snapshot.QwenAsr.ChoiceId, "observed_asr"));
        result.AddRange(snapshot.Proposals.Select(item => (item.ProposalId, "generated_interpretation")));
        return result;
    }

    public void Dispose() => _client.Dispose();
}
