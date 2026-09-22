using System.Text.Json;
using System.Security.Cryptography;

namespace Trazio.AsistenteReunion.Core;

public sealed class SettingsStore(string path)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private static readonly byte[] Entropy = "Trazio.AsistenteReunion.settings.v1"u8.ToArray();

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) return new();
        var protectedBytes = await File.ReadAllBytesAsync(path, cancellationToken);
        var json = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
        return JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new();
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        var json = JsonSerializer.SerializeToUtf8Bytes(settings, Options);
        var protectedBytes = ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser);
        await File.WriteAllBytesAsync(temporary, protectedBytes, cancellationToken);
        File.Move(temporary, path, true);
    }
}
