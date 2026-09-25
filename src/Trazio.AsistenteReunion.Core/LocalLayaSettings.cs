namespace Trazio.AsistenteReunion.Core;

public sealed record LocalLayaSettings(
    string NodeExecutablePath,
    string SidecarDirectory,
    string ModelDirectory,
    string ModelVersion);

public static class LocalLayaSettingsPolicy
{
    private static readonly string[] RequiredModelFiles =
    [
        "laya.onnx", "laya.onnx.data", "laya_config.json",
        Path.Combine("tokenizer", "tokenizer.json"),
        Path.Combine("tokenizer", "tokenizer_config.json")
    ];

    public static LocalLayaSettings Create(
        string nodeExecutablePath, string sidecarDirectory,
        string modelDirectory, string modelVersion)
    {
        var node = RequiredAbsoluteFile(nodeExecutablePath, "Selecciona un node.exe existente.");
        if (!string.Equals(Path.GetFileName(node), "node.exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("El ejecutable de Laya debe ser node.exe.", nameof(nodeExecutablePath));
        var sidecar = RequiredAbsoluteDirectory(sidecarDirectory, "Selecciona la carpeta del sidecar Laya.");
        var model = RequiredAbsoluteDirectory(modelDirectory, "Selecciona la carpeta del modelo Laya.");
        if (string.IsNullOrWhiteSpace(modelVersion) || modelVersion.Length > 120 ||
            modelVersion.Any(char.IsControl))
            throw new ArgumentException("Indica la versión exacta del modelo Laya.", nameof(modelVersion));
        return new(node, sidecar, model, modelVersion.Trim());
    }

    public static void ValidateInstalled(LocalLayaSettings settings, bool deepScanDependencies = false)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _ = Create(settings.NodeExecutablePath, settings.SidecarDirectory,
            settings.ModelDirectory, settings.ModelVersion);
        var script = Path.Combine(settings.SidecarDirectory, "server.mjs");
        var package = Path.Combine(settings.SidecarDirectory, "package.json");
        var lockfile = Path.Combine(settings.SidecarDirectory, "package-lock.json");
        var dependencies = Path.Combine(settings.SidecarDirectory, "node_modules");
        if (!File.Exists(script) || !File.Exists(package) || !File.Exists(lockfile) ||
            !Directory.Exists(Path.Combine(dependencies, "@receptron", "laya")) ||
            !Directory.Exists(Path.Combine(dependencies, "@huggingface", "tokenizers")))
            throw new InvalidOperationException("Falta el sidecar o sus dependencias locales. Instálalas manualmente antes de evaluar; Trazio no las descarga.");
        ValidateLocalPath(script);
        ValidateLocalPath(package);
        ValidateLocalPath(lockfile);
        ValidateLocalPath(Path.Combine(dependencies, "@receptron", "laya"));
        ValidateLocalPath(Path.Combine(dependencies, "@huggingface", "tokenizers"));
        if (deepScanDependencies) ValidateDependencyTree(dependencies);
        foreach (var relative in RequiredModelFiles)
        {
            var path = Path.Combine(settings.ModelDirectory, relative);
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
                throw new InvalidOperationException("Falta un archivo del paquete local Laya. Selecciona un modelo completo; Trazio no lo descarga.");
            ValidateLocalPath(path);
        }
    }

    private static string RequiredAbsoluteFile(string path, string message)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !File.Exists(path))
            throw new ArgumentException(message);
        var resolved = Path.GetFullPath(path);
        ValidateLocalPath(resolved);
        return resolved;
    }

    private static string RequiredAbsoluteDirectory(string path, string message)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !Directory.Exists(path))
            throw new ArgumentException(message);
        var resolved = Path.GetFullPath(path);
        ValidateLocalPath(resolved);
        return resolved;
    }

    public static void ValidateLocalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new InvalidOperationException("Laya requiere rutas locales absolutas.");
        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\", StringComparison.Ordinal) ||
            full.StartsWith(@"//", StringComparison.Ordinal))
            throw new InvalidOperationException("Laya no admite rutas de red ni rutas de dispositivo.");
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root) || new DriveInfo(root).DriveType == DriveType.Network)
            throw new InvalidOperationException("Laya no admite unidades de red.");
        for (string? current = full; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Laya no admite enlaces simbólicos ni puntos de análisis en sus rutas.");
        }
    }

    private static void ValidateDependencyTree(string root)
    {
        ValidateLocalPath(root);
        const int maximumEntries = 100_000;
        var count = 0;
        var pending = new Queue<DirectoryInfo>();
        pending.Enqueue(new DirectoryInfo(root));
        while (pending.Count > 0)
        {
            foreach (var entry in pending.Dequeue().EnumerateFileSystemInfos())
            {
                if (++count > maximumEntries)
                    throw new InvalidOperationException("Las dependencias Laya superan el límite de verificación local.");
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Laya no admite enlaces simbólicos en sus dependencias.");
                if ((entry.Attributes & FileAttributes.Directory) != 0)
                    pending.Enqueue(new DirectoryInfo(entry.FullName));
            }
        }
    }
}
