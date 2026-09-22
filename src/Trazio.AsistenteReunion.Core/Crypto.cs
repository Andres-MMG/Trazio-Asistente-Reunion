using System.Security.Cryptography;

namespace Trazio.AsistenteReunion.Core;

public sealed record EncryptedPayload(byte[] Nonce, byte[] Ciphertext, byte[] Tag);

public interface IContentProtector
{
    EncryptedPayload Protect(ReadOnlySpan<byte> plaintext, string associatedData);
    byte[] Unprotect(EncryptedPayload payload, string associatedData);
}

public sealed class AesContentProtector(byte[] key) : IContentProtector, IDisposable
{
    private readonly byte[] _key = key.Length == 32 ? [.. key] : throw new ArgumentException("Se requiere una clave de 256 bits.", nameof(key));

    public EncryptedPayload Protect(ReadOnlySpan<byte> plaintext, string associatedData)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_key, tag.Length);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, System.Text.Encoding.UTF8.GetBytes(associatedData));
        return new(nonce, ciphertext, tag);
    }

    public byte[] Unprotect(EncryptedPayload payload, string associatedData)
    {
        var plaintext = new byte[payload.Ciphertext.Length];
        using var aes = new AesGcm(_key, payload.Tag.Length);
        aes.Decrypt(payload.Nonce, payload.Ciphertext, payload.Tag, plaintext, System.Text.Encoding.UTF8.GetBytes(associatedData));
        return plaintext;
    }

    public void Dispose() => CryptographicOperations.ZeroMemory(_key);
}

public static class MasterKeyStore
{
    private static readonly byte[] Entropy = "Trazio.AsistenteReunion.v1"u8.ToArray();

    public static byte[] LoadOrCreate(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
            return ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);

        var key = RandomNumberGenerator.GetBytes(32);
        var protectedKey = ProtectedData.Protect(key, Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(path, protectedKey);
        return key;
    }
}
