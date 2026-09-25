using System.Globalization;
using System.Text;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.App;

internal static class LayaEvaluationPresenter
{
    public static string RecommendationName(TranscriptRefinementEvaluation evaluation) =>
        evaluation.Recommendation switch
        {
            RefinementEvaluationRecommendation.KeepOriginal => "mantener original",
            RefinementEvaluationRecommendation.HumanReview => "requiere revisión humana",
            RefinementEvaluationRecommendation.SuggestProposal =>
                "considerar " + ChoiceName(evaluation, evaluation.RecommendedProposalId ?? string.Empty),
            _ => "juicio no reconocido"
        };

    public static string Describe(IReadOnlyList<TranscriptRefinementEvaluation> evaluations)
    {
        if (evaluations.Count == 0) return "Sin evaluaciones guardadas.";
        var shown = evaluations.Reverse().Take(5).ToArray();
        var text = new StringBuilder("Evaluaciones guardadas (juicios textuales, no prueba de fidelidad acústica):");
        foreach (var evaluation in shown)
        {
            text.AppendLine().Append(evaluation.CreatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture))
                .Append(" · ").Append(evaluation.Evaluator?.Model ?? "regla local")
                .Append(' ').Append(evaluation.Evaluator?.ModelVersion ?? evaluation.PolicyVersion)
                .Append(" · ").Append(RecommendationName(evaluation));
            if (evaluation.Evaluator?.JevFallbackReason is { } reason)
                text.Append(" · respaldo por ").Append(reason switch
                {
                    RefinementJevFallbackReason.NotInstalled => "Laya no instalado",
                    RefinementJevFallbackReason.Capacity => "capacidad local",
                    _ => "indisponibilidad local"
                });
            if (evaluation.Snapshot.QwenAsr is { } qwen)
                text.AppendLine().Append("Fuentes: Whisper original (observación acústica); Qwen3-ASR configurado (observación acústica) · ")
                    .Append(qwen.ModelIdentity).Append(" · ").Append(qwen.Source == AudioSourceKind.Microphone ? "micrófono" : "audio del equipo")
                    .Append(" · ").Append(qwen.Start).Append('–').Append(qwen.End)
                    .Append(" · huella ").Append(qwen.ModelHash)
                    .Append(". Las propuestas son interpretaciones generadas; ninguna es verdad acústica comprobada.");
            if (evaluation.Judgment is not { } judgment) continue;
            text.AppendLine().Append("Elección textual del modelo (no adopción): ").Append(ChoiceName(evaluation, judgment.ChoiceId))
                .Append("; confianza ").Append(Number(judgment.ChoiceConfidence));
            foreach (var choice in judgment.ChoiceProbabilities.OrderBy(item => item.Key, StringComparer.Ordinal))
                text.AppendLine().Append("  P(").Append(ChoiceName(evaluation, choice.Key))
                    .Append(") = ").Append(Number(choice.Value));
            foreach (var nouls in judgment.CandidateNouls)
                text.AppendLine().Append("  ").Append(ChoiceName(evaluation, nouls.ProposalId))
                    .Append(": preservación semántica ").Append(Number(nouls.SemanticPreservation))
                    .Append("; hechos no respaldados ").Append(Number(nouls.UnsupportedFacts));
        }
        if (evaluations.Count > shown.Length)
            text.AppendLine().Append("Se muestran las 5 evaluaciones más recientes de ")
                .Append(evaluations.Count.ToString(CultureInfo.CurrentCulture)).Append('.');
        text.AppendLine().Append("Laya y Jev nunca aceptan ni modifican texto automáticamente.");
        return text.ToString();
    }

    private static string ChoiceName(TranscriptRefinementEvaluation evaluation, string choiceId)
    {
        if (choiceId == TranscriptRefinementEvaluationPolicy.KeepOriginalChoice) return "original";
        if (choiceId == TranscriptRefinementEvaluationPolicy.HumanReviewChoice) return "revisión humana";
        if (choiceId == evaluation.Snapshot.QwenAsr?.ChoiceId) return "Qwen3-ASR configurado · observación acústica";
        var index = evaluation.Snapshot.Proposals.ToList().FindIndex(item => item.ProposalId == choiceId);
        return index < 0 ? "opción desconocida" : $"propuesta {index + 1} · interpretación generada";
    }

    private static string Number(double number) => number.ToString("F4", CultureInfo.InvariantCulture);
}
