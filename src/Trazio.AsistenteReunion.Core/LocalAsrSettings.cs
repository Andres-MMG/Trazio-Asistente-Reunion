namespace Trazio.AsistenteReunion.Core;

public sealed record LocalAsrSettings(
    string LlamaServerPath,
    string QwenModelPath,
    string MultimodalProjectorPath);

public static class LocalAsrSettingsPolicy
{
    public static LocalAsrSettings Create(
        string llamaServerPath,
        string qwenModelPath,
        string multimodalProjectorPath)
    {
        var server = ExistingFile(llamaServerPath, "Selecciona llama-server.exe.");
        var model = ExistingFile(qwenModelPath, "Selecciona el modelo GGUF de Qwen3-ASR.");
        var projector = ExistingFile(multimodalProjectorPath, "Selecciona el archivo mmproj de Qwen3-ASR.");
        if (!string.Equals(Path.GetExtension(server), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("El ejecutable de llama.cpp debe ser un archivo .exe.");
        if (!string.Equals(Path.GetExtension(model), ".gguf", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetExtension(projector), ".gguf", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("El modelo y el proyector multimodal deben ser archivos GGUF.");
        if (!Path.GetFileName(projector).StartsWith("mmproj", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("El proyector multimodal debe corresponder a un archivo mmproj de Qwen3-ASR.");
        return new(server, model, projector);
    }

    private static string ExistingFile(string path, string message)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException(message);
        var candidate = path.Trim();
        if (!Path.IsPathFullyQualified(candidate) || candidate.StartsWith(@"\\", StringComparison.Ordinal) ||
            candidate.StartsWith("//", StringComparison.Ordinal))
            throw new InvalidOperationException("Qwen3-ASR necesita una ruta local absoluta, no una ruta de red.");
        var fullPath = Path.GetFullPath(candidate);
        if (!File.Exists(fullPath)) throw new FileNotFoundException(message, fullPath);
        return fullPath;
    }
}