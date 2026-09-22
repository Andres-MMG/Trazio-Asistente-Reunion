using System.Security.Cryptography;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.Tests;

public sealed class CryptoTests
{
    [Fact]
    public void Protect_RoundTrip_ReturnsOriginalBytes()
    {
        using var protector = new AesContentProtector(RandomNumberGenerator.GetBytes(32));
        var encrypted = protector.Protect("private transcript"u8, "segment:1:text");
        Assert.Equal("private transcript"u8.ToArray(), protector.Unprotect(encrypted, "segment:1:text"));
    }

    [Fact]
    public void Unprotect_TamperedCiphertext_Throws()
    {
        using var protector = new AesContentProtector(RandomNumberGenerator.GetBytes(32));
        var encrypted = protector.Protect("private transcript"u8, "segment:1:text");
        encrypted.Ciphertext[0] ^= 1;
        Assert.Throws<AuthenticationTagMismatchException>(() => protector.Unprotect(encrypted, "segment:1:text"));
    }

    [Fact]
    public void Unprotect_WrongAssociatedData_Throws()
    {
        using var protector = new AesContentProtector(RandomNumberGenerator.GetBytes(32));
        var encrypted = protector.Protect("private transcript"u8, "segment:1:text");
        Assert.Throws<AuthenticationTagMismatchException>(() => protector.Unprotect(encrypted, "segment:2:text"));
    }
}
