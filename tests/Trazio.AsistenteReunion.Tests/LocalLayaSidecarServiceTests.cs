using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Trazio.AsistenteReunion.App;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.Tests;

public sealed class LocalLayaSidecarServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "trazio-laya-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task EvaluateAsync_UsesAuthenticatedHealthAndStopsOwnedProcessAfterInference()
    {
        var settings = CompleteBundle();
        var process = new FakeProcess();
        var launcher = new FakeLauncher(process);
        var handler = new FakeHandler((request, _) =>
        {
            Assert.Equal("127.0.0.1", request.RequestUri!.Host);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal(64, request.Headers.Authorization.Parameter!.Length);
            return Task.FromResult(request.RequestUri.AbsolutePath == "/health"
                ? Response("{\"status\":\"ready\",\"model\":\"laya\",\"model_version\":\"v1\"}")
                : Response(ValidEvaluation()));
        });
        var service = new LocalLayaSidecarService(launcher, new FakeHandlerFactory(handler), () => 44001);

        var result = await service.EvaluateAsync(settings, Snapshot, Context, Approve, CancellationToken.None);

        Assert.Equal("laya", result.Evaluator.Model);
        Assert.Equal("v1", result.Evaluator.ModelVersion);
        Assert.StartsWith("sha256:", result.Evaluator.BundleFingerprint, StringComparison.Ordinal);
        Assert.Equal(2, handler.Calls);
        Assert.Equal(1, launcher.Starts);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task EvaluateAsync_SameVersionWithChangedBundleProducesDifferentFingerprint()
    {
        var settings = CompleteBundle();
        static LocalLayaSidecarService Service() => new(
            new FakeLauncher(new FakeProcess()),
            new FakeHandlerFactory(new FakeHandler((request, _) => Task.FromResult(
                request.RequestUri!.AbsolutePath == "/health"
                    ? Response("{\"status\":\"ready\",\"model\":\"laya\",\"model_version\":\"v1\"}")
                    : Response(ValidEvaluation())))), () => 44001);

        var first = await Service().EvaluateAsync(settings, Snapshot, Context, Approve, CancellationToken.None);
        File.WriteAllBytes(Path.Combine(settings.ModelDirectory, "laya.onnx.data"), [7, 8, 9]);
        var second = await Service().EvaluateAsync(settings, Snapshot, Context, Approve, CancellationToken.None);

        Assert.Equal(first.Evaluator.ModelVersion, second.Evaluator.ModelVersion);
        Assert.NotEqual(first.Evaluator.BundleFingerprint, second.Evaluator.BundleFingerprint);
    }

    [Fact]
    public async Task EvaluateAsync_BundleChangedAfterHealthFailsBeforeTranscriptPost()
    {
        var settings = CompleteBundle();
        var process = new FakeProcess();
        var handler = new FakeHandler((request, _) =>
        {
            Assert.Equal("/health", request.RequestUri!.AbsolutePath);
            return Task.FromResult(Response("{\"status\":\"ready\",\"model\":\"laya\",\"model_version\":\"v1\"}"));
        });
        var fingerprinter = new ChangedAfterReadyFingerprinter();
        var service = new LocalLayaSidecarService(new FakeLauncher(process),
            new FakeHandlerFactory(handler), () => 44001, fingerprinter);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.EvaluateAsync(settings, Snapshot, Context, Approve, CancellationToken.None));

        Assert.Equal(1, handler.Calls);
        Assert.True(process.Disposed);
        Assert.True(fingerprinter.LeaseDisposed);
    }

    [Fact]
    public async Task EvaluateAsync_DeniedConsentShowsExactPathsAndHashWithoutStartingProcess()
    {
        var settings = CompleteBundle();
        var launcher = new FakeLauncher(new FakeProcess());
        var handler = new FakeHandler((_, _) => throw new Xunit.Sdk.XunitException("No debe haber tráfico."));
        var service = new LocalLayaSidecarService(launcher, new FakeHandlerFactory(handler), () => 44001);
        LocalLayaExecutionPreview? preview = null;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.EvaluateAsync(settings, Snapshot, Context, (shown, _) =>
            {
                preview = shown;
                return Task.FromResult(false);
            }, CancellationToken.None));

        Assert.NotNull(preview);
        Assert.Equal(settings.NodeExecutablePath, preview.NodeExecutablePath);
        Assert.Equal(Path.Combine(settings.SidecarDirectory, "server.mjs"), preview.ScriptPath);
        Assert.Equal(settings.ModelDirectory, preview.ModelDirectory);
        Assert.StartsWith("sha256:", preview.BundleFingerprint, StringComparison.Ordinal);
        Assert.Contains("conectarse a Internet", LocalLayaExecutionPreview.TrustWarning, StringComparison.Ordinal);
        Assert.Contains("no certifica", LocalLayaExecutionPreview.TrustWarning, StringComparison.Ordinal);
        Assert.Equal(0, launcher.Starts);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public void Settings_RejectsNetworkAndReparsePaths()
    {
        Assert.Throws<InvalidOperationException>(() =>
            LocalLayaSettingsPolicy.ValidateLocalPath(@"\\servidor\recurso\node.exe"));
        var settings = CompleteBundle();
        var link = Path.Combine(_root, "model-link");
        try { Directory.CreateSymbolicLink(link, settings.ModelDirectory); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        { return; }
        Assert.Throws<InvalidOperationException>(() =>
            LocalLayaSettingsPolicy.ValidateLocalPath(Path.Combine(link, "laya.onnx")));
    }

    [Fact]
    public async Task EvaluateAsync_MissingModelFileFailsBeforeProcessOrNetwork()
    {
        var settings = CompleteBundle();
        File.Delete(Path.Combine(settings.ModelDirectory, "laya.onnx.data"));
        var launcher = new FakeLauncher(new FakeProcess());
        var handler = new FakeHandler((_, _) => throw new Xunit.Sdk.XunitException("No debe haber tráfico."));
        var service = new LocalLayaSidecarService(launcher, new FakeHandlerFactory(handler), () => 44001);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.EvaluateAsync(settings, Snapshot, Context, Approve, CancellationToken.None));

        Assert.Equal(0, launcher.Starts);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task EvaluateAsync_InvalidHealthStopsProcessWithoutSendingTranscript()
    {
        var settings = CompleteBundle();
        var process = new FakeProcess();
        var handler = new FakeHandler((_, _) => Task.FromResult(Response("{\"status\":\"ready\",\"model\":\"wrong\",\"model_version\":\"v1\"}")));
        var service = new LocalLayaSidecarService(new FakeLauncher(process), new FakeHandlerFactory(handler), () => 44001);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.EvaluateAsync(settings, Snapshot, Context, Approve, CancellationToken.None));

        Assert.Equal(1, handler.Calls);
        Assert.True(process.Disposed);
    }

    [Fact]
    public void IntelligenceSettings_WarnsThatLocalCodeMayAccessTextAndNetwork()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        string? xaml = null;
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "src", "Trazio.AsistenteReunion.App", "MainWindow.xaml");
            if (File.Exists(candidate)) { xaml = File.ReadAllText(candidate); break; }
            current = current.Parent;
        }
        Assert.NotNull(xaml);
        Assert.Contains("podrían leer texto y conectarse a Internet", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("No descarga archivos ni envía texto fuera del equipo", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Presenter_LabelsRawJudgmentsAsTextualAndNeverAutomatic()
    {
        var judgment = new RefinementEvaluationJudgment("proposal", 0.90,
            new Dictionary<string, double>
            {
                ["proposal"] = 0.84,
                [TranscriptRefinementEvaluationPolicy.KeepOriginalChoice] = 0.10,
                [TranscriptRefinementEvaluationPolicy.HumanReviewChoice] = 0.06
            }, [new("proposal", 0.98, 0.40)]);
        var evaluation = new TranscriptRefinementEvaluation("evaluation", Snapshot,
            new("laya", "v1", "questions-v1"), TranscriptRefinementEvaluationPolicy.Version,
            judgment, RefinementEvaluationRecommendation.HumanReview, null, DateTimeOffset.UtcNow);

        var text = LayaEvaluationPresenter.Describe([evaluation]);

        Assert.Contains("0.8400", text, StringComparison.Ordinal);
        Assert.Contains("0.9800", text, StringComparison.Ordinal);
        Assert.Contains("0.4000", text, StringComparison.Ordinal);
        Assert.Contains("no prueba de fidelidad acústica", text, StringComparison.Ordinal);
        Assert.Contains("nunca acepta", text, StringComparison.Ordinal);
    }

    private static Task<bool> Approve(LocalLayaExecutionPreview _, CancellationToken token) =>
        Task.FromResult(true);

    private LocalLayaSettings CompleteBundle()
    {
        var sidecar = Directory.CreateDirectory(Path.Combine(_root, "sidecar")).FullName;
        var model = Directory.CreateDirectory(Path.Combine(_root, "model")).FullName;
        var node = Path.Combine(_root, "node.exe");
        File.WriteAllBytes(node, [1]);
        File.WriteAllBytes(Path.Combine(sidecar, "server.mjs"), [1]);
        File.WriteAllBytes(Path.Combine(sidecar, "package.json"), [1]);
        File.WriteAllBytes(Path.Combine(sidecar, "package-lock.json"), [1]);
        Directory.CreateDirectory(Path.Combine(sidecar, "node_modules", "@receptron", "laya"));
        Directory.CreateDirectory(Path.Combine(sidecar, "node_modules", "@huggingface", "tokenizers"));
        foreach (var name in new[] { "laya.onnx", "laya.onnx.data", "laya_config.json",
            Path.Combine("tokenizer", "tokenizer.json"), Path.Combine("tokenizer", "tokenizer_config.json") })
        {
            var file = Path.Combine(model, name);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, [1]);
        }
        return LocalLayaSettingsPolicy.Create(node, sidecar, model, "v1");
    }

    private static readonly RefinementEvaluationSnapshot Snapshot = new("batch", "session",
        AudioSourceKind.Microphone, TimeSpan.Zero, TimeSpan.FromSeconds(2), "original",
        [new("proposal", "propuesta")]);
    private static readonly TranscriptContextPacket Context = new([], [], []);

    private static string ValidEvaluation() => JsonSerializer.Serialize(new
    {
        model = "laya", model_version = "v1", answers = new Dictionary<string, object>
        {
            ["preference"] = new { type = "choice", choice = "proposal", confidence = 0.90,
                probabilities = new Dictionary<string, double>
                {
                    ["proposal"] = 0.84,
                    [TranscriptRefinementEvaluationPolicy.KeepOriginalChoice] = 0.10,
                    [TranscriptRefinementEvaluationPolicy.HumanReviewChoice] = 0.06
                } },
            ["semantic_0"] = new { type = "noul", noul = 0.98 },
            ["unsupported_0"] = new { type = "noul", noul = 0.40 }
        }
    });

    private static HttpResponseMessage Response(string text) => new(HttpStatusCode.OK)
    { Content = new StringContent(text, Encoding.UTF8, "application/json") };

    private sealed class ChangedAfterReadyFingerprinter : ILocalLayaBundleFingerprinter
    {
        public bool LeaseDisposed { get; private set; }
        public Task<ILocalLayaBundleLease> AcquireAsync(LocalLayaSettings settings, CancellationToken token) =>
            Task.FromResult<ILocalLayaBundleLease>(new Lease(this));
        private sealed class Lease(ChangedAfterReadyFingerprinter owner) : ILocalLayaBundleLease
        {
            private int _checks;
            public string Fingerprint => "sha256:" + new string('A', 64);
            public Task VerifyUnchangedAsync(CancellationToken token)
            {
                if (++_checks > 1) throw new InvalidDataException("El paquete cambió después de health.");
                return Task.CompletedTask;
            }
            public ValueTask DisposeAsync() { owner.LeaseDisposed = true; return ValueTask.CompletedTask; }
        }
    }

    private sealed class FakeProcess : ILocalLayaProcess
    {
        public int Id => 42;
        public bool HasExited => false;
        public bool Disposed { get; private set; }
        public void Kill() => Disposed = true;
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class FakeLauncher(FakeProcess process) : ILocalLayaProcessLauncher
    {
        public int Starts { get; private set; }
        public ILocalLayaProcess Start(LocalLayaSettings settings, int port, string token)
        { Starts++; return process; }
    }

    private sealed class FakeHandlerFactory(FakeHandler handler) : ILocalLayaHttpHandlerFactory
    { public HttpMessageHandler Create(int port, ILocalLayaProcess process) => handler; }

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback)
        : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Calls++; return callback(request, token); }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
