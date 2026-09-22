using System.Net;
using System.Security.Cryptography;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.Tests;

public sealed class WhisperModelStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "trazio-model-tests-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] Payload = "verified model fixture"u8.ToArray();
    private static WhisperModelDefinition Definition => new("fixture.bin", new Uri("https://example.invalid/model"), Payload.Length, Convert.ToHexString(SHA256.HashData(Payload)));
    private WhisperModelStore Create(byte[] body, Action? requested = null) => new(new HttpClient(new Handler(() => { requested?.Invoke(); return new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }; })), _root, Definition);

    [Fact]
    public async Task Download_ValidContent_InstallsAtomicallyAndReusesCache()
    {
        var requests = 0;
        var store = Create(Payload, () => requests++);
        var path = await store.DownloadAsync();
        Assert.Equal(Payload, await File.ReadAllBytesAsync(path));
        Assert.Equal(path, await store.DownloadAsync());
        Assert.Equal(1, requests);
        Assert.Empty(Directory.GetFiles(_root, "*.partial"));
    }

    [Theory]
    [InlineData("wrong model content!!!")]
    [InlineData("short")]
    [InlineData("much larger than the expected model fixture")]
    public async Task Download_InvalidContent_RejectsAndCleansUp(string text)
    {
        var store = Create(System.Text.Encoding.UTF8.GetBytes(text));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.DownloadAsync());
        Assert.Empty(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task Download_CorruptExistingFile_DoesNotOverwriteIt()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, Definition.FileName);
        await File.WriteAllTextAsync(path, "corrupted");
        await Assert.ThrowsAsync<InvalidDataException>(() => Create(Payload).DownloadAsync());
        Assert.Equal("corrupted", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Download_CancelledDuringTransfer_CleansUpAndCanRetry()
    {
        using var cancellation = new CancellationTokenSource();
        var store = Create(Payload);
        var progress = new InlineProgress(_ => cancellation.Cancel());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.DownloadAsync(progress, cancellation.Token));
        Assert.Empty(Directory.GetFiles(_root));
        Assert.True(File.Exists(await store.DownloadAsync()));
    }

    [Fact]
    public async Task Resolve_CustomPath_PreservesUserChoiceWithoutNetwork()
    {
        Directory.CreateDirectory(_root);
        var custom = Path.Combine(_root, "custom.bin");
        await File.WriteAllBytesAsync(custom, Payload);
        var store = Create(Payload, () => throw new InvalidOperationException("Network must not be used"));
        Assert.Equal(custom, await store.ResolveAsync(custom, Path.Combine(_root, "bundled")));
    }

    [Fact]
    public async Task Resolve_VerifiedBundledModel_SelectsWithoutDownload()
    {
        var bundle = Path.Combine(_root, "bundle");
        Directory.CreateDirectory(bundle);
        var path = Path.Combine(bundle, Definition.FileName);
        await File.WriteAllBytesAsync(path, Payload);
        Assert.Equal(path, await Create(Payload).ResolveAsync(null, bundle));
    }

    [Fact]
    public async Task Download_WrongHashWithCorrectSize_RejectsContent()
    {
        var altered = Payload.ToArray();
        altered[0] ^= 1;
        await Assert.ThrowsAsync<InvalidDataException>(() => Create(altered).DownloadAsync());
        Assert.Empty(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task Download_ConcurrentRequests_UsesSingleTransfer()
    {
        var requests = 0;
        var store = Create(Payload, () => requests++);
        var paths = await Task.WhenAll(store.DownloadAsync(), store.DownloadAsync());
        Assert.Equal(paths[0], paths[1]);
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task Resolve_CorruptManagedModel_RejectsWithoutNetwork()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, Definition.FileName), "corrupt");
        await Assert.ThrowsAsync<InvalidDataException>(() => Create(Payload).ResolveAsync(null, Path.Combine(_root, "bundle")));
    }

    [Fact]
    public async Task Download_HttpFailure_AllowsRetryWithoutPartialFile()
    {
        var attempts = 0;
        using var client = new HttpClient(new Handler(() => ++attempts == 1
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) }));
        var store = new WhisperModelStore(client, _root, Definition);
        await Assert.ThrowsAsync<HttpRequestException>(() => store.DownloadAsync());
        Assert.Empty(Directory.GetFiles(_root));
        Assert.True(File.Exists(await store.DownloadAsync()));
        Assert.Equal(2, attempts);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private sealed class Handler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response());
    }
    private sealed class InlineProgress(Action<ModelDownloadProgress> report) : IProgress<ModelDownloadProgress>
    {
        public void Report(ModelDownloadProgress value) => report(value);
    }
}
