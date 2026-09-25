using System.Text;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.Tests;

public sealed class ExternalAiProviderSettingsTests
{
    [Fact]
    public void Create_HttpsEndpoint_NormalizesConfiguration()
    {
        var settings = ExternalAiProviderPolicy.Create(
            " https://api.example.com/v1/chat/completions ",
            " meeting-model ",
            "secret-key");

        Assert.Equal("https://api.example.com/v1/chat/completions", settings.Endpoint);
        Assert.Equal("meeting-model", settings.Model);
        Assert.Equal("secret-key", settings.ApiKey);
        Assert.False(settings.IsLoopback);
    }

    [Theory]
    [InlineData("http://localhost:11434/v1/chat/completions")]
    [InlineData("http://127.0.0.1:1234/v1/chat/completions")]
    [InlineData("http://[::1]:8080/v1/chat/completions")]
    public void Create_HttpLoopback_AllowsSelfHostedApi(string endpoint)
    {
        var settings = ExternalAiProviderPolicy.Create(endpoint, "local-model", null);

        Assert.True(settings.IsLoopback);
        Assert.Null(settings.ApiKey);
    }

    [Theory]
    [InlineData("http://api.example.com/v1/chat/completions")]
    [InlineData("ftp://api.example.com/v1/chat/completions")]
    [InlineData("https://user:password@api.example.com/v1/chat/completions")]
    [InlineData("https://api.example.com/v1/chat/completions?secret=value")]
    [InlineData("https://api.example.com/v1/chat/completions#fragment")]
    public void Create_UnsafeEndpoint_RejectsConfiguration(string endpoint)
    {
        Assert.Throws<InvalidOperationException>(() =>
            ExternalAiProviderPolicy.Create(endpoint, "model", "secret"));
    }

    [Fact]
    public void Create_RemoteEndpointWithoutApiKey_RejectsConfiguration()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ExternalAiProviderPolicy.Create("https://api.example.com/v1/chat/completions", "model", null));
    }

    [Fact]
    public async Task SettingsStore_SaveAndLoad_ProtectsProviderCredentialsAtRest()
    {
        var path = Path.Combine(Path.GetTempPath(), $"trazio-ai-settings-{Guid.NewGuid():N}.dat");
        try
        {
            var store = new SettingsStore(path);
            var provider = ExternalAiProviderPolicy.Create(
                "https://api.example.com/v1/chat/completions",
                "private-model",
                "private-api-key");
            var settings = new AppSettings(ExternalAiProvider: provider);

            await store.SaveAsync(settings);

            Assert.Equal(settings, await store.LoadAsync());
            var raw = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path));
            Assert.DoesNotContain(provider.Endpoint, raw, StringComparison.Ordinal);
            Assert.DoesNotContain(provider.Model, raw, StringComparison.Ordinal);
            Assert.DoesNotContain(provider.ApiKey!, raw, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ResolveApiKey_BlankReplacement_PreservesExistingSecret()
    {
        var existing = ExternalAiProviderPolicy.Create(
            "https://api.example.com/v1/chat/completions",
            "model",
            "existing-secret");

        Assert.Equal("existing-secret", ExternalAiProviderPolicy.ResolveApiKey(existing, "  "));
        Assert.Equal("new-secret", ExternalAiProviderPolicy.ResolveApiKey(existing, "new-secret"));
    }
}
