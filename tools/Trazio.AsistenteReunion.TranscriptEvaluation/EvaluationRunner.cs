using System.Security.Cryptography;
using System.Text.Json;

namespace Trazio.AsistenteReunion.TranscriptEvaluation;

public static class EvaluationRunner
{
    public const string RequiredConsentScope = "stage-9-offline-transcription-evaluation";
    private const int MaxManifestBytes = 8 * 1024 * 1024;
    private const int MaxClips = 10_000;
    private const int MaxConfigurations = 32;

    public static EvaluationReport EvaluateFile(string manifestPath, DateTimeOffset now)
    {
        if (!Path.GetFileName(manifestPath).EndsWith(".evaluation.private.json", StringComparison.OrdinalIgnoreCase))
            throw new EvaluationException("El manifiesto debe terminar en .evaluation.private.json para quedar excluido de Git.");
        var root = Path.GetDirectoryName(Path.GetFullPath(manifestPath))
            ?? throw new EvaluationException("Ruta de manifiesto inválida.");
        RejectReparsePointsInAncestors(manifestPath);
        using var stream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 or > MaxManifestBytes)
            throw new EvaluationException("El manifiesto está vacío o excede 8 MiB.");
        var manifest = JsonSerializer.Deserialize<EvaluationManifest>(stream, EvaluationJson.Options)
            ?? throw new EvaluationException("El manifiesto no contiene datos.");
        return Evaluate(manifest, root, now);
    }

    public static EvaluationReport Evaluate(EvaluationManifest manifest, string root, DateTimeOffset now)
    {
        if (manifest.SchemaVersion != 1 ||
            manifest.MeaningErrorKindsVersion != MeaningErrorVocabulary.Version ||
            manifest.Consent is null || manifest.Clips is null ||
            manifest.Configurations is null || manifest.Hypotheses is null)
            throw new EvaluationException("Esquema 9.2 incompleto o no compatible.");
        var consent = manifest.Consent;
        if (string.IsNullOrWhiteSpace(consent.ConsentId) || consent.Scope != RequiredConsentScope ||
            consent.GrantedAtUtc == default || consent.ExpiresAtUtc <= consent.GrantedAtUtc ||
            consent.GrantedAtUtc > now || consent.ExpiresAtUtc <= now)
            throw new EvaluationException("Falta consentimiento vigente y específico para esta evaluación.");
        CheckHash(root, consent.EvidenceRelativePath, consent.EvidenceSha256, false);

        if (manifest.Clips.Count is 0 or > MaxClips || manifest.Configurations.Count is 0 or > MaxConfigurations ||
            manifest.Hypotheses.Count != manifest.Clips.Count * manifest.Configurations.Count)
            throw new EvaluationException("Cada configuración debe cubrir exactamente los mismos fragmentos.");

        var clipMap = new Dictionary<string, EvaluationClip>(StringComparer.Ordinal);
        foreach (var clip in manifest.Clips)
        {
            if (clip is null || string.IsNullOrWhiteSpace(clip.ClipId) || !clipMap.TryAdd(clip.ClipId, clip) ||
                clip.ConsentReference != consent.ConsentId || string.IsNullOrWhiteSpace(clip.ApprovedReferenceText) ||
                string.IsNullOrWhiteSpace(clip.ReferenceApprovedBy) ||
                clip.ReferenceApprovedAtUtc < consent.GrantedAtUtc || clip.ReferenceApprovedAtUtc > now ||
                clip.Tags is null || clip.Tags.Count == 0 || clip.Tags.Any(string.IsNullOrWhiteSpace) ||
                clip.ApprovedReferenceText.Length > TranscriptMetrics.MaxTextCharacters ||
                TranscriptMetrics.Normalize(clip.ApprovedReferenceText).Length == 0)
                throw new EvaluationException("Fragmento sin consentimiento, texto de referencia aprobado o etiquetas válidas.");
            CheckHash(root, clip.AudioRelativePath, clip.AudioSha256, true);
        }

        var configurationMap = new Dictionary<string, EvaluationConfiguration>(StringComparer.Ordinal);
        foreach (var configuration in manifest.Configurations)
        {
            if (configuration is null || string.IsNullOrWhiteSpace(configuration.ConfigurationId) ||
                !configurationMap.TryAdd(configuration.ConfigurationId, configuration) ||
                configuration.TranscriptionKind is not ("acoustic" or "refined") ||
                string.IsNullOrWhiteSpace(configuration.ModelId) ||
                string.IsNullOrWhiteSpace(configuration.HardwareId) ||
                !IsSha256(configuration.ModelSha256) || !IsSha256(configuration.SettingsSha256))
                throw new EvaluationException("Configuración sin procedencia verificable o duplicada.");
        }

        var groups = new Dictionary<string, List<EvaluationHypothesis>>(StringComparer.Ordinal);
        var pairs = new HashSet<(string ClipId, string ConfigurationId)>();
        long estimatedComparisonCells = 0;
        foreach (var hypothesis in manifest.Hypotheses)
        {
            if (hypothesis is null || !clipMap.ContainsKey(hypothesis.ClipId) ||
                !configurationMap.ContainsKey(hypothesis.ConfigurationId) ||
                !pairs.Add((hypothesis.ClipId, hypothesis.ConfigurationId)) ||
                string.IsNullOrWhiteSpace(hypothesis.Text) ||
                !double.IsFinite(hypothesis.LatencyMs) || hypothesis.LatencyMs < 0 ||
                hypothesis.PeakWorkingSetBytes <= 0 || hypothesis.MeaningReview is null ||
                string.IsNullOrWhiteSpace(hypothesis.MeaningReview.ReviewedBy) ||
                hypothesis.MeaningReview.ReviewedAtUtc < consent.GrantedAtUtc ||
                hypothesis.MeaningReview.ReviewedAtUtc > now ||
                hypothesis.MeaningReview.ErrorKinds is null ||
                hypothesis.MeaningReview.ErrorKinds.Any(kind => !MeaningErrorVocabulary.IsValid(kind)) ||
                hypothesis.MeaningReview.ErrorKinds.Distinct(StringComparer.Ordinal).Count() !=
                    hypothesis.MeaningReview.ErrorKinds.Count ||
                hypothesis.MeaningReview.ChangedMeaning != (hypothesis.MeaningReview.ErrorKinds.Count > 0))
                throw new EvaluationException("Hipótesis, medición o revisión humana inválida o duplicada.");
            estimatedComparisonCells += TranscriptMetrics.EstimateComparisonCells(
                clipMap[hypothesis.ClipId].ApprovedReferenceText, hypothesis.Text);
            if (estimatedComparisonCells > TranscriptMetrics.MaxComparisonCellsPerManifest)
                throw new EvaluationException("El manifiesto supera el límite total de cálculo.");
            if (!groups.TryGetValue(hypothesis.ConfigurationId, out var group))
                groups[hypothesis.ConfigurationId] = group = [];
            group.Add(hypothesis);
        }

        var reports = new List<ConfigurationReport>(manifest.Configurations.Count);
        foreach (var configuration in manifest.Configurations)
        {
            var group = groups[configuration.ConfigurationId];
            var wordErrors = 0L;
            var referenceWords = 0L;
            var characterErrors = 0L;
            var referenceCharacters = 0L;
            var meaningErrorKinds = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var hypothesis in group)
            {
                var reference = clipMap[hypothesis.ClipId].ApprovedReferenceText;
                var errors = TranscriptMetrics.CountErrors(reference, hypothesis.Text);
                wordErrors += errors.WordErrors;
                referenceWords += errors.ReferenceWords;
                characterErrors += errors.CharacterErrors;
                referenceCharacters += errors.ReferenceCharacters;
                foreach (var kind in hypothesis.MeaningReview.ErrorKinds.Distinct(StringComparer.Ordinal))
                    meaningErrorKinds[kind] = meaningErrorKinds.GetValueOrDefault(kind) + 1;
            }

            reports.Add(new ConfigurationReport(
                configuration.ConfigurationId,
                configuration.TranscriptionKind,
                configuration.ModelId,
                configuration.ModelSha256,
                configuration.SettingsSha256,
                configuration.HardwareId,
                group.Count,
                (double)wordErrors / referenceWords,
                (double)characterErrors / referenceCharacters,
                group.Count(item => item.MeaningReview.ChangedMeaning),
                meaningErrorKinds,
                TranscriptMetrics.Percentile(group.Select(item => item.LatencyMs).ToArray(), .5),
                TranscriptMetrics.Percentile(group.Select(item => item.LatencyMs).ToArray(), .95),
                (long)Math.Round(TranscriptMetrics.Percentile(group.Select(item => (double)item.PeakWorkingSetBytes).ToArray(), .5)),
                (long)Math.Round(TranscriptMetrics.Percentile(group.Select(item => (double)item.PeakWorkingSetBytes).ToArray(), .95))));
        }

        return new EvaluationReport(1, MeaningErrorVocabulary.Version, consent.ConsentId, clipMap.Count,
            TranscriptMetrics.NormalizationVersion, reports);
    }

    private static void CheckHash(string root, string relativePath, string expectedSha256, bool requireWav)
    {
        if (!IsSha256(expectedSha256))
            throw new EvaluationException("Falta SHA-256 válido de audio o evidencia de consentimiento.");
        var path = ResolvePrivatePath(root, relativePath);
        if (requireWav && !Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase))
            throw new EvaluationException("Los fragmentos deben ser WAV.");
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (requireWav)
        {
            Span<byte> header = stackalloc byte[12];
            if (file.Read(header) != 12 || !header[..4].SequenceEqual("RIFF"u8) ||
                !header[8..12].SequenceEqual("WAVE"u8))
                throw new EvaluationException("El audio no tiene encabezado WAV RIFF válido.");
            file.Position = 0;
        }
        if (!Convert.ToHexString(SHA256.HashData(file)).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new EvaluationException("El SHA-256 del archivo privado no coincide con el manifiesto.");
    }

    private static string ResolvePrivatePath(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) ||
            relativePath.Contains(':', StringComparison.Ordinal))
            throw new EvaluationException("Se requieren rutas relativas privadas.");
        var parts = relativePath.Replace('\\', '/').Split('/');
        if (parts.Any(part => part is "" or "." or ".."))
            throw new EvaluationException("La ruta privada no puede atravesar carpetas.");
        var rootPath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(rootPath, Path.Combine(parts)));
        if (!path.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
            throw new EvaluationException("El archivo debe permanecer dentro de la carpeta privada del manifiesto.");
        RejectReparsePointsInAncestors(path);
        return path;
    }

    private static void RejectReparsePointsInAncestors(string path)
    {
        string? current = Path.GetFullPath(path);
        while (current is not null)
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new EvaluationException("No se admiten enlaces o puntos de reanálisis en los archivos privados.");
            var parent = Path.GetDirectoryName(current);
            if (parent == current)
                break;
            current = parent;
        }
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);
}
