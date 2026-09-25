using System.IO;

namespace Trazio.AsistenteReunion.App;

public sealed record LocalAsrExecutionPreview(
    string ServerExecutablePath,
    string ServerSha256,
    string ModelPath,
    string ModelSha256,
    string MultimodalProjectorPath,
    string MultimodalProjectorSha256)
{
    public const string TrustWarning =
        "Se ejecutará un programa local externo y recibirá el audio de este fragmento. " +
        "Este permiso NO garantiza aislamiento: el ejecutable y sus dependencias podrían acceder a otros archivos " +
        "o conectarse a Internet. Autoriza solo archivos de una fuente en la que confíes. " +
        "Las huellas identifican archivos, pero no certifican que sean seguros.";
}

internal static class LocalAsrExecutionGuard
{
    public static string ValidateLocalFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new InvalidOperationException("Selecciona una ruta local absoluta para Qwen3-ASR.");
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
            throw new InvalidOperationException("Qwen3-ASR no admite rutas de red ni rutas de dispositivo.");
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("No se encontró un archivo de Qwen3-ASR.", fullPath);
        var drive = new DriveInfo(Path.GetPathRoot(fullPath)!);
        if (drive.DriveType is DriveType.Network or DriveType.NoRootDirectory)
            throw new InvalidOperationException("Qwen3-ASR no admite archivos en unidades de red.");
        for (var directory = Path.GetDirectoryName(fullPath); directory is not null;
             directory = Path.GetDirectoryName(directory))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Qwen3-ASR no admite rutas con enlaces o puntos de reanálisis.");
            if (Path.GetPathRoot(directory) == directory) break;
        }
        if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Qwen3-ASR no admite archivos enlazados.");
        return fullPath;
    }

}
