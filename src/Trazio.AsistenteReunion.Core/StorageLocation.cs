using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace Trazio.AsistenteReunion.Core;

public sealed record StorageManagedFile(string RelativePath, long Length, string Sha256);
public sealed record StorageMigrationIntent(
    string Id,
    string SourceRoot,
    string TargetRoot,
    IReadOnlyList<StorageManagedFile>? Manifest = null);
public sealed record StorageLocationState(
    string? ActiveRoot,
    StorageMigrationIntent? Pending,
    string? CleanupPendingSource);

public interface IDataRootLocator
{
    StorageLocationState Load();
    void Schedule(StorageMigrationIntent intent);
    void PrepareMigration(StorageMigrationIntent intent);
    void CancelPending();
    void Commit(StorageMigrationIntent intent);
    void CompleteCleanup();
}

public sealed class RegistryDataRootLocator : IDataRootLocator
{
    public const string RegistryPath = @"Software\Trazio\AsistenteReunion";
    private const string StateValue = "StorageStateV1";
    private const int CurrentVersion = 1;

    private sealed record StateDocument(
        int Version,
        string? ActiveRoot,
        StorageMigrationIntent? Pending,
        string? CleanupPendingSource);

    public StorageLocationState Load()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: false);
        if (key?.GetValue(StateValue) is string json)
        {
            var document = JsonSerializer.Deserialize<StateDocument>(json)
                ?? throw new InvalidOperationException("El estado de la ubicación de almacenamiento no es válido.");
            if (document.Version != CurrentVersion)
                throw new InvalidOperationException($"Versión no compatible de la ubicación de almacenamiento: {document.Version}.");
            return new(document.ActiveRoot, document.Pending, document.CleanupPendingSource);
        }

        // Compatibility with the pre-document locator. The next mutation rewrites it atomically.
        var active = key?.GetValue("ActiveRoot") as string;
        var pendingId = key?.GetValue("PendingId") as string;
        var source = key?.GetValue("PendingSource") as string;
        var target = key?.GetValue("PendingTarget") as string;
        var pending = string.IsNullOrWhiteSpace(pendingId) || string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target)
            ? null
            : new StorageMigrationIntent(pendingId, source, target);
        return new(active, pending, key?.GetValue("CleanupPendingSource") as string);
    }

    public void Schedule(StorageMigrationIntent intent)
    {
        var state = Load();
        if (state.Pending is not null)
            throw new InvalidOperationException("Ya hay un traslado de almacenamiento pendiente.");
        Save(new(state.ActiveRoot, intent, null));
    }

    public void PrepareMigration(StorageMigrationIntent intent)
    {
        var state = Load();
        RequirePending(state, intent);
        Save(state with { Pending = intent });
    }

    public void CancelPending()
    {
        var state = Load();
        if (state.CleanupPendingSource is not null)
            throw new InvalidOperationException("El traslado de almacenamiento ya fue confirmado y debe finalizar la limpieza.");
        Save(state with { Pending = null });
    }

    public void Commit(StorageMigrationIntent intent)
    {
        var state = Load();
        RequirePending(state, intent);
        if (intent.Manifest is null)
            throw new InvalidOperationException("Se necesita un registro de traslado verificado antes de confirmar la operación.");

        // One registry value makes ActiveRoot, Pending, and CleanupPendingSource one durable transition.
        // Pending intentionally remains until verified cleanup has completed.
        Save(new(intent.TargetRoot, intent, intent.SourceRoot));
    }

    public void CompleteCleanup()
    {
        var state = Load();
        Save(new(state.ActiveRoot, null, null));
    }

    private static void RequirePending(StorageLocationState state, StorageMigrationIntent intent)
    {
        if (state.Pending?.Id != intent.Id)
            throw new InvalidOperationException("El traslado de almacenamiento pendiente ya no corresponde a esta operación.");
    }

    private static void Save(StorageLocationState state)
    {
        var document = new StateDocument(CurrentVersion, state.ActiveRoot, state.Pending, state.CleanupPendingSource);
        var json = JsonSerializer.Serialize(document);
        using var key = Registry.CurrentUser.CreateSubKey(RegistryPath, writable: true);
        key.SetValue(StateValue, json, RegistryValueKind.String);
        key.DeleteValue("ActiveRoot", false);
        key.DeleteValue("PendingId", false);
        key.DeleteValue("PendingSource", false);
        key.DeleteValue("PendingTarget", false);
        key.DeleteValue("CleanupPendingSource", false);
        key.Flush();
    }
}

public sealed record StorageDriveStatus(bool IsLocalFixedDrive, bool IsReady, long AvailableBytes);

public interface IStoragePlatform
{
    StorageDriveStatus InspectDrive(string path);
    void VerifyWritableDirectory(string path);
}

public sealed class WindowsStoragePlatform : IStoragePlatform
{
    public StorageDriveStatus InspectDrive(string path)
    {
        var root = Path.GetPathRoot(path) ?? throw new InvalidOperationException("La carpeta de almacenamiento no tiene una unidad raíz.");
        var drive = new DriveInfo(root);
        return new(drive.DriveType == DriveType.Fixed, drive.IsReady, drive.IsReady ? drive.AvailableFreeSpace : 0);
    }

    public void VerifyWritableDirectory(string path)
    {
        Directory.CreateDirectory(path);
        var probe = Path.Combine(path, $".trazio-write-{Guid.NewGuid():N}.tmp");
        try
        {
            using var stream = new FileStream(
                probe,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1,
                FileOptions.WriteThrough | FileOptions.DeleteOnClose);
            stream.WriteByte(0x54);
            stream.Flush(true);
        }
        finally
        {
            try { File.Delete(probe); }
            catch (FileNotFoundException) { }
        }
    }
}

public interface IStorageMigrationObserver
{
    void BeforeCopy(string relativePath);
}

public sealed class StorageMigrationService
{
    private static readonly string[] RootFiles =
    [
        "trazio-transcripts.db", "trazio-transcripts.db-wal", "trazio-transcripts.db-shm",
        "master.key", "settings.dat", "WHISPER-MODEL-LICENSE.txt", "THIRD-PARTY-NOTICES.md"
    ];

    private readonly IDataRootLocator _locator;
    private readonly IStoragePlatform _platform;
    private readonly IStorageMigrationObserver? _observer;

    public StorageMigrationService(
        IDataRootLocator locator,
        IStoragePlatform platform,
        IStorageMigrationObserver? observer = null)
    {
        _locator = locator;
        _platform = platform;
        _observer = observer;
    }

    public string ResolveActiveRoot(string defaultRoot, string installDirectory)
    {
        var state = _locator.Load();
        if (state.Pending is not null)
            return MigratePending(state, installDirectory);

        var active = Normalize(state.ActiveRoot ?? defaultRoot);
        if (state.ActiveRoot is not null)
            ValidateAvailableActiveRoot(active);
        else
            Directory.CreateDirectory(active);

        // Legacy cleanup state had no exact content manifest. Preserve old data rather than guessing.
        if (!string.IsNullOrWhiteSpace(state.CleanupPendingSource))
            _locator.CompleteCleanup();

        return active;
    }

    public StorageMigrationIntent Schedule(string sourceRoot, string targetRoot, string installDirectory)
    {
        if (!Path.IsPathFullyQualified(targetRoot) || targetRoot.StartsWith(@"\\", StringComparison.Ordinal))
            throw new InvalidOperationException("Elige una carpeta absoluta en una unidad local fija. No se admiten rutas de red ni relativas.");

        var source = Normalize(sourceRoot);
        var target = Normalize(targetRoot);
        ValidateTarget(source, target, installDirectory, allowMigrationFiles: false, intent: null);
        var intent = new StorageMigrationIntent(Guid.NewGuid().ToString("N"), source, target);
        _locator.Schedule(intent);
        return intent;
    }

    public void CancelScheduled(StorageMigrationIntent intent)
    {
        var state = _locator.Load();
        if (state.Pending?.Id != intent.Id)
            throw new InvalidOperationException("El cambio de almacenamiento pendiente ya no corresponde a esta solicitud.");
        if (state.CleanupPendingSource is not null || PathsEqual(state.ActiveRoot, intent.TargetRoot))
            throw new InvalidOperationException("El traslado de almacenamiento ya fue confirmado y no se puede cancelar.");

        RemovePendingTargetFiles(state.Pending);
        _locator.CancelPending();
    }

    public static string TargetUnderParent(string selectedParent) =>
        Path.Combine(Normalize(selectedParent), ApplicationPaths.ProductFolderName);

    private string MigratePending(StorageLocationState state, string installDirectory)
    {
        var intent = state.Pending!;
        var source = Normalize(intent.SourceRoot);
        var target = Normalize(intent.TargetRoot);

        if (intent.Manifest is null)
        {
            ValidateAvailableActiveRoot(source);
            var prepared = intent with { Manifest = BuildManifest(source) };
            _locator.PrepareMigration(prepared);
            intent = prepared;
            state = _locator.Load();
        }

        var manifest = intent.Manifest
            ?? throw new InvalidOperationException("El traslado de almacenamiento pendiente no tiene un registro verificado.");

        if (PathsEqual(state.ActiveRoot, target))
        {
            VerifyTarget(target, manifest);
            CleanupSource(source, target, manifest);
            _locator.CompleteCleanup();
            return target;
        }

        ValidateAvailableActiveRoot(source);
        ValidateSourceManifest(source, manifest);
        ValidateTarget(source, target, installDirectory, allowMigrationFiles: true, intent);
        var remaining = manifest
            .Where(entry => !FileMatches(Path.Combine(target, entry.RelativePath), entry))
            .Sum(entry => entry.Length);
        EnsureFreeSpace(target, remaining);

        foreach (var entry in manifest)
            CopyVerified(entry, source, target, intent.Id);

        VerifyTarget(target, manifest);
        _locator.Commit(intent);
        try
        {
            CleanupSource(source, target, manifest);
            _locator.CompleteCleanup();
        }
        catch
        {
            // The single state document still contains active target + pending intent + cleanup source.
            // Startup resumes exact-manifest cleanup without copying or deleting unrelated contents.
        }

        return target;
    }

    private void ValidateTarget(
        string source,
        string target,
        string installDirectory,
        bool allowMigrationFiles,
        StorageMigrationIntent? intent)
    {
        ValidateAbsoluteLocalFolder(target);
        var root = Path.GetPathRoot(target)!;
        if (PathsEqual(target, root))
            throw new InvalidOperationException("Elige una carpeta y no la raíz de una unidad.");
        if (PathsEqual(source, target))
            throw new InvalidOperationException("La nueva carpeta de almacenamiento es la misma que la actual.");
        if (IsWithin(source, target) || IsWithin(target, source))
            throw new InvalidOperationException("Las carpetas de almacenamiento actual y nueva no pueden estar una dentro de la otra.");

        var install = Normalize(installDirectory);
        if (PathsEqual(target, install) || IsWithin(target, install) || IsWithin(install, target))
            throw new InvalidOperationException("El almacenamiento no puede estar dentro de la carpeta de instalación ni contenerla.");

        var existed = Directory.Exists(target);
        if (existed)
        {
            var entries = Directory.EnumerateFileSystemEntries(target, "*", SearchOption.AllDirectories).ToArray();
            if (!allowMigrationFiles && entries.Length > 0)
                throw new InvalidOperationException("La carpeta Trazio de destino debe estar vacía.");
            if (allowMigrationFiles &&
                (intent?.Manifest is null || entries.Any(path => !IsRecognizableMigrationEntry(path, target, intent))))
                throw new InvalidOperationException("El destino contiene archivos que no pertenecen a este traslado pendiente de Trazio.");
        }

        try { _platform.VerifyWritableDirectory(target); }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"No se puede escribir en la carpeta de almacenamiento: {ex.Message}", ex);
        }

        if (!existed && !Directory.EnumerateFileSystemEntries(target).Any())
            Directory.Delete(target);
    }

    private void ValidateAvailableActiveRoot(string active)
    {
        ValidateAbsoluteLocalFolder(active);
        if (!Directory.Exists(active))
            throw new InvalidOperationException($"La carpeta de almacenamiento configurada no está disponible: {active}. Vuelve a conectar la unidad o cancela la ubicación configurada antes de continuar.");
        try { _platform.VerifyWritableDirectory(active); }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"No se puede escribir en la carpeta de almacenamiento configurada: {active}. {ex.Message}", ex);
        }
    }

    private void ValidateAbsoluteLocalFolder(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
            throw new InvalidOperationException("Elige una carpeta absoluta en una unidad local fija. No se admiten rutas de red ni relativas.");

        StorageDriveStatus drive;
        try { drive = _platform.InspectDrive(path); }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"La unidad de almacenamiento no está disponible: {ex.Message}", ex);
        }

        if (!drive.IsLocalFixedDrive || !drive.IsReady)
            throw new InvalidOperationException("Elige una unidad local fija y disponible. No se admiten unidades de red, extraíbles ni no disponibles.");
    }

    private void EnsureFreeSpace(string target, long required)
    {
        var drive = _platform.InspectDrive(target);
        if (drive.AvailableBytes < required)
            throw new InvalidOperationException($"El destino necesita al menos {required / 1_000_000.0:F1} MB libres, pero solo hay {drive.AvailableBytes / 1_000_000.0:F1} MB disponibles.");
    }

    private void CopyVerified(
        StorageManagedFile entry,
        string sourceRoot,
        string targetRoot,
        string migrationId)
    {
        _observer?.BeforeCopy(entry.RelativePath);
        var source = Path.Combine(sourceRoot, entry.RelativePath);
        if (!FileMatches(source, entry))
            throw new IOException($"El origen del traslado cambió después de preparar el registro: {entry.RelativePath}");

        var destination = Path.Combine(targetRoot, entry.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(destination))
        {
            if (FileMatches(destination, entry))
                return;
            throw new IOException($"Conflicto durante el traslado: el archivo de destino es diferente: {entry.RelativePath}");
        }

        var partial = destination + $".{migrationId}.partial";
        if (File.Exists(partial))
            File.Delete(partial);

        CopyStreaming(source, partial);
        if (!FileMatches(partial, entry))
            throw new IOException($"Falló la verificación del traslado: {entry.RelativePath}");
        File.Move(partial, destination, overwrite: false);
    }

    private static void CopyStreaming(string source, string destination)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, FileOptions.SequentialScan);
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length, FileOptions.SequentialScan | FileOptions.WriteThrough);
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                output.Write(buffer, 0, read);
            output.Flush(true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static IReadOnlyList<StorageManagedFile> BuildManifest(string root) =>
        EnumerateManagedFiles(root)
            .Select(file => new StorageManagedFile(
                Path.GetRelativePath(root, file.FullName),
                file.Length,
                ComputeSha256(file.FullName)))
            .OrderBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static void ValidateSourceManifest(string source, IReadOnlyList<StorageManagedFile> manifest)
    {
        foreach (var entry in manifest)
            if (!FileMatches(Path.Combine(source, entry.RelativePath), entry))
                throw new IOException($"El origen del traslado cambió después de preparar el registro: {entry.RelativePath}");
    }

    private static void VerifyTarget(string target, IReadOnlyList<StorageManagedFile> manifest)
    {
        foreach (var entry in manifest)
            if (!FileMatches(Path.Combine(target, entry.RelativePath), entry))
                throw new IOException($"Falló la verificación del traslado de {entry.RelativePath}.");
    }

    private static void CleanupSource(
        string source,
        string target,
        IReadOnlyList<StorageManagedFile> manifest)
    {
        if (!Directory.Exists(source))
            return;

        foreach (var entry in manifest)
        {
            var destination = Path.Combine(target, entry.RelativePath);
            if (!FileMatches(destination, entry))
                throw new IOException($"La limpieza de los datos anteriores se detuvo porque la copia de destino no está verificada: {entry.RelativePath}");

            var sourceFile = Path.Combine(source, entry.RelativePath);
            if (!File.Exists(sourceFile))
                continue;
            if (!FileMatches(sourceFile, entry))
                continue; // Changed content is no longer the exact manifested file and must be preserved.
            File.Delete(sourceFile);
        }

        foreach (var name in new[] { "audio", "models" })
            RemoveEmptyTree(Path.Combine(source, name));
        if (!Directory.EnumerateFileSystemEntries(source).Any())
            Directory.Delete(source);
    }

    private static void RemovePendingTargetFiles(StorageMigrationIntent intent)
    {
        if (!Directory.Exists(intent.TargetRoot) || intent.Manifest is null)
            return;

        foreach (var entry in intent.Manifest)
        {
            var destination = Path.Combine(intent.TargetRoot, entry.RelativePath);
            var partial = destination + $".{intent.Id}.partial";
            if (File.Exists(partial))
                File.Delete(partial);
            if (FileMatches(destination, entry))
                File.Delete(destination);
        }

        RemoveEmptyTree(intent.TargetRoot);
    }

    private static bool IsRecognizableMigrationEntry(
        string path,
        string targetRoot,
        StorageMigrationIntent intent)
    {
        var relative = Path.GetRelativePath(targetRoot, path);
        if (Directory.Exists(path))
            return intent.Manifest!.Any(entry =>
                entry.RelativePath.StartsWith(relative + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

        var partialSuffix = $".{intent.Id}.partial";
        var finalRelative = relative.EndsWith(partialSuffix, StringComparison.Ordinal)
            ? relative[..^partialSuffix.Length]
            : relative;
        return intent.Manifest!.Any(entry =>
            string.Equals(entry.RelativePath, finalRelative, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<FileInfo> EnumerateManagedFiles(string root)
    {
        if (!Directory.Exists(root))
            yield break;

        foreach (var name in RootFiles)
        {
            var path = Path.Combine(root, name);
            if (File.Exists(path))
                yield return new FileInfo(path);
        }

        foreach (var directoryName in new[] { "audio", "models" })
        {
            var directory = Path.Combine(root, directoryName);
            if (!Directory.Exists(directory))
                continue;
            foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.AllDirectories))
                if (!IsExcludedTemporary(file.Name))
                    yield return file;
        }
    }

    private static bool FileMatches(string path, StorageManagedFile entry)
    {
        var file = new FileInfo(path);
        return file.Exists &&
               file.Length == entry.Length &&
               string.Equals(ComputeSha256(path), entry.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static bool IsExcludedTemporary(string name) =>
        name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        return root is not null && string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
            ? root
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool PathsEqual(string? first, string? second) =>
        first is not null &&
        second is not null &&
        string.Equals(Normalize(first), Normalize(second), StringComparison.OrdinalIgnoreCase);

    private static bool IsWithin(string child, string parent) =>
        Normalize(child).StartsWith(Normalize(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static void RemoveEmptyTree(string path)
    {
        if (!Directory.Exists(path))
            return;
        foreach (var directory in Directory.EnumerateDirectories(path))
            RemoveEmptyTree(directory);
        if (!Directory.EnumerateFileSystemEntries(path).Any())
            Directory.Delete(path);
    }
}
