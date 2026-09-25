using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.App;

internal sealed record LocalLayaExecutionPreview(
    string NodeExecutablePath, string ScriptPath, string ModelDirectory,
    string DependenciesDirectory, string BundleFingerprint)
{
    public const string TrustWarning =
        "Node, el sidecar y sus dependencias son código local ejecutable. Si no son confiables, pueden leer la transcripción y conectarse a Internet. El SHA-256 identifica archivos, pero no certifica que sean seguros.";
}

internal interface ILocalLayaProcess : IAsyncDisposable
{
    int Id { get; }
    bool HasExited { get; }
    void Kill();
}

internal interface ILocalLayaProcessLauncher
{
    ILocalLayaProcess Start(LocalLayaSettings settings, int port, string token);
}

internal interface ILocalLayaHttpHandlerFactory
{
    HttpMessageHandler Create(int port, ILocalLayaProcess process);
}

internal sealed class LocalLayaSidecarService(
    ILocalLayaProcessLauncher? launcher = null,
    ILocalLayaHttpHandlerFactory? handlerFactory = null,
    Func<int>? portFactory = null,
    ILocalLayaBundleFingerprinter? fingerprinter = null)
{
    private readonly ILocalLayaProcessLauncher _launcher = launcher ?? new NodeLayaProcessLauncher();
    private readonly ILocalLayaHttpHandlerFactory _handlerFactory = handlerFactory ?? new OwnedLayaHttpHandlerFactory();
    private readonly Func<int> _portFactory = portFactory ?? ReservePort;
    private readonly ILocalLayaBundleFingerprinter _fingerprinter = fingerprinter ?? new LocalLayaBundleFingerprinter();

    public async Task<RefinementLayaEvaluation> EvaluateAsync(
        LocalLayaSettings settings, RefinementEvaluationSnapshot snapshot,
        TranscriptContextPacket context,
        Func<LocalLayaExecutionPreview, CancellationToken, Task<bool>> authorizeExecution,
        CancellationToken cancellationToken)
    {
        settings = LocalLayaSettingsPolicy.Create(
            settings.NodeExecutablePath, settings.SidecarDirectory,
            settings.ModelDirectory, settings.ModelVersion);
        await Task.Run(() => LocalLayaSettingsPolicy.ValidateInstalled(
            settings, deepScanDependencies: true), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(authorizeExecution);
        await using var bundle = await _fingerprinter.AcquireAsync(settings, cancellationToken);
        var preview = new LocalLayaExecutionPreview(
            settings.NodeExecutablePath, Path.Combine(settings.SidecarDirectory, "server.mjs"),
            settings.ModelDirectory, Path.Combine(settings.SidecarDirectory, "node_modules"),
            bundle.Fingerprint);
        if (!await authorizeExecution(preview, cancellationToken))
            throw new OperationCanceledException("La ejecución local no fue autorizada.", cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Run(() => LocalLayaSettingsPolicy.ValidateInstalled(
            settings, deepScanDependencies: true), cancellationToken);
        await bundle.VerifyUnchangedAsync(cancellationToken);
        var port = _portFactory();
        if (port is < 1 or > 65535) throw new InvalidOperationException("No se pudo reservar el puerto local de Laya.");
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await using var process = _launcher.Start(settings, port, token);
        using var handler = _handlerFactory.Create(port, process);
        using var healthClient = new HttpClient(handler, disposeHandler: false)
        { Timeout = TimeSpan.FromSeconds(5) };
        using var evaluator = new RefinementLayaEvaluator(handler);
        var root = $"http://127.0.0.1:{port}";
        await WaitReadyAsync(healthClient, new Uri(root + "/health"), token,
            settings.ModelVersion, process, cancellationToken);
        await bundle.VerifyUnchangedAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (process.HasExited) throw new RefinementLayaException(RefinementLayaFailure.Unavailable, "Laya local terminó antes de evaluar.");
        var endpoint = RefinementLayaSettings.Create(root + "/v1/system-one", token, settings.ModelVersion);
        var result = await evaluator.EvaluateAsync(snapshot, context, endpoint, cancellationToken);
        await bundle.VerifyUnchangedAsync(cancellationToken);
        return result with
        {
            Evaluator = result.Evaluator with { BundleFingerprint = bundle.Fingerprint }
        };
    }

    private static async Task WaitReadyAsync(HttpClient client, Uri endpoint, string token,
        string version, ILocalLayaProcess process, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(40));
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited) throw new RefinementLayaException(RefinementLayaFailure.Unavailable, "Laya local no pudo cargar el modelo o sus dependencias.");
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    if (response.Content.Headers.ContentLength > 512)
                        throw new InvalidDataException("La respuesta de salud local no es válida.");
                    await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                    var bytes = await ConservativeTranscriptRefinementGenerator.ReadBoundedResponseAsync(
                        stream, new byte[512], 512, timeout.Token);
                    try
                    {
                        using var json = JsonDocument.Parse(bytes);
                        var root = json.RootElement;
                        if (root.ValueKind != JsonValueKind.Object ||
                            !root.TryGetProperty("status", out var status) || status.GetString() != "ready" ||
                            !root.TryGetProperty("model", out var model) || model.GetString() != "laya" ||
                            !root.TryGetProperty("model_version", out var modelVersion) ||
                            modelVersion.GetString() != version)
                            throw new InvalidDataException("La identidad del modelo Laya local no coincide.");
                        return;
                    }
                    catch (JsonException) { throw new InvalidDataException("La respuesta de salud local no es válida."); }
                    finally { CryptographicOperations.ZeroMemory(bytes); }
                }
                if (response.StatusCode == HttpStatusCode.InternalServerError)
                    throw new RefinementLayaException(RefinementLayaFailure.Unavailable, "Laya local no pudo cargar el modelo.");
                if (response.StatusCode != HttpStatusCode.ServiceUnavailable)
                    throw new InvalidOperationException("Laya local rechazó la comprobación de salud.");
            }
            catch (HttpRequestException) when (!process.HasExited) { }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested && !timeout.IsCancellationRequested) { }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
            { throw new RefinementLayaException(RefinementLayaFailure.Unavailable, "Laya local no estuvo listo dentro del tiempo permitido."); }
            try { await Task.Delay(150, timeout.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { throw new RefinementLayaException(RefinementLayaFailure.Unavailable, "Laya local no estuvo listo dentro del tiempo permitido."); }
        }
    }

    private static int ReservePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

internal sealed class NodeLayaProcessLauncher : ILocalLayaProcessLauncher
{
    public ILocalLayaProcess Start(LocalLayaSettings settings, int port, string token)
    {
        var info = new ProcessStartInfo(settings.NodeExecutablePath)
        {
            WorkingDirectory = settings.SidecarDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        info.ArgumentList.Add(Path.Combine(settings.SidecarDirectory, "server.mjs"));
        info.Environment["TRAZIO_LAYA_PORT"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        info.Environment["TRAZIO_LAYA_TOKEN"] = token;
        info.Environment["LAYA_MODEL_DIR"] = settings.ModelDirectory;
        info.Environment["LAYA_MODEL_VERSION"] = settings.ModelVersion;
        info.Environment["LAYA_THREADS"] = "4";
        var process = Process.Start(info) ?? throw new InvalidOperationException("No se pudo iniciar Laya local.");
        return new OwnedNodeLayaProcess(process);
    }
}

internal sealed class OwnedNodeLayaProcess(Process process) : ILocalLayaProcess
{
    public int Id => process.Id;
    public bool HasExited => process.HasExited;
    public void Kill()
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }
    public async ValueTask DisposeAsync()
    {
        try
        {
            Kill();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { }
        }
        finally { process.Dispose(); }
    }
}

internal sealed class OwnedLayaHttpHandlerFactory : ILocalLayaHttpHandlerFactory
{
    public HttpMessageHandler Create(int port, ILocalLayaProcess process) => new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        UseCookies = false,
        ConnectCallback = async (_, cancellationToken) =>
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
                var clientPort = ((IPEndPoint)socket.LocalEndPoint!).Port;
                var ownerVerified = false;
                for (var attempt = 0; attempt < 12; attempt++)
                {
                    if (process.HasExited || !WindowsTcpListenerOwner.IsOwnedBy(port, process.Id)) break;
                    if (WindowsTcpListenerOwner.IsEstablishedConnectionOwnedBy(port, clientPort, process.Id))
                    { ownerVerified = true; break; }
                    await Task.Delay(25, cancellationToken);
                }
                if (!ownerVerified)
                {
                    process.Kill();
                    throw new HttpRequestException("La conexión local de Laya no pertenece al proceso iniciado.");
                }
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    };
}
