using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.App;

public sealed record LlamaCppAsrRuntimeOptions(
    string ServerExecutablePath,
    string MultimodalProjectorPath,
    TimeSpan StartupTimeout,
    int ContextSize = 8_192,
    int GpuLayers = -1)
{
    public static LlamaCppAsrRuntimeOptions Create(
        string serverExecutablePath,
        string multimodalProjectorPath,
        TimeSpan? startupTimeout = null,
        int contextSize = 8_192,
        int gpuLayers = -1)
    {
        var executable = LocalAsrExecutionGuard.ValidateLocalFile(NormalizeExistingFile(serverExecutablePath, "Selecciona llama-server.exe."));
        var projector = LocalAsrExecutionGuard.ValidateLocalFile(NormalizeExistingFile(multimodalProjectorPath, "Selecciona el archivo mmproj de Qwen3-ASR."));
        var timeout = startupTimeout ?? TimeSpan.FromMinutes(3);
        if (timeout < TimeSpan.FromSeconds(10) || timeout > TimeSpan.FromMinutes(15))
            throw new ArgumentOutOfRangeException(nameof(startupTimeout));
        if (contextSize is < 1_024 or > 262_144)
            throw new ArgumentOutOfRangeException(nameof(contextSize));
        if (gpuLayers < -1)
            throw new ArgumentOutOfRangeException(nameof(gpuLayers));
        return new(executable, projector, timeout, contextSize, gpuLayers);
    }

    private static string NormalizeExistingFile(string path, string missingMessage)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException(missingMessage);
        var candidate = path.Trim();
        if (!Path.IsPathFullyQualified(candidate) || candidate.StartsWith(@"\\", StringComparison.Ordinal) ||
            candidate.StartsWith("//", StringComparison.Ordinal))
            throw new InvalidOperationException("Qwen3-ASR necesita una ruta local absoluta, no una ruta de red.");
        var fullPath = Path.GetFullPath(candidate);
        if (!File.Exists(fullPath)) throw new FileNotFoundException(missingMessage, fullPath);
        return fullPath;
    }
}

public interface ITranscriptionExecutionAuthorization : IAsyncDisposable
{
    Task<ITranscriptionTransport> StartAsync(CancellationToken cancellationToken);
}

public interface ISelectedSegmentPreauthorizationFactory
{
    Task<ITranscriptionExecutionAuthorization> AuthorizeAsync(
        string modelPath, string language, CancellationToken cancellationToken);
}

public sealed class LlamaCppAsrTransportFactory(
    LlamaCppAsrRuntimeOptions options,
    Func<LocalAsrExecutionPreview, CancellationToken, Task<bool>> authorizeExecution)
    : ITranscriptionTransportFactory, ISelectedSegmentPreauthorizationFactory
{
    public Task<ITranscriptionTransport> StartAsync(
        string modelPath,
        string language,
        CancellationToken cancellationToken) =>
        StartAsync(modelPath, language, initialPrompt: null, cancellationToken);

    public async Task<ITranscriptionTransport> StartAsync(
        string modelPath,
        string language,
        string? initialPrompt,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(initialPrompt))
            throw new InvalidOperationException("Qwen3-ASR mediante llama.cpp todavía no admite el prompt del diccionario de Whisper.");
        await using var authorized = await AuthorizeAsync(modelPath, language, cancellationToken);
        return await authorized.StartAsync(cancellationToken);
    }

    public async Task<ITranscriptionExecutionAuthorization> AuthorizeAsync(
        string modelPath, string language, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorizeExecution);
        var fullModelPath = LocalAsrExecutionGuard.ValidateLocalFile(modelPath);
        var executablePath = LocalAsrExecutionGuard.ValidateLocalFile(options.ServerExecutablePath);
        var projectorPath = LocalAsrExecutionGuard.ValidateLocalFile(options.MultimodalProjectorPath);
        FileStream? executableLock = null;
        FileStream? modelLock = null;
        FileStream? projectorLock = null;
        try
        {
            // Retain the file locks across consent, audio extraction and process load.
            executableLock = OpenModelReadLock(executablePath);
            modelLock = OpenModelReadLock(fullModelPath);
            projectorLock = OpenModelReadLock(projectorPath);
            var preview = new LocalAsrExecutionPreview(
                executablePath, await ComputeFileHashAsync(executableLock, cancellationToken),
                fullModelPath, await ComputeFileHashAsync(modelLock, cancellationToken),
                projectorPath, await ComputeFileHashAsync(projectorLock, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            if (!await authorizeExecution(preview, cancellationToken))
                throw new OperationCanceledException("No se autorizó el proceso local Qwen3-ASR.", cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return new AuthorizedRun(this, preview, language, executableLock, modelLock, projectorLock);
        }
        catch
        {
            if (projectorLock is not null) await projectorLock.DisposeAsync();
            if (modelLock is not null) await modelLock.DisposeAsync();
            if (executableLock is not null) await executableLock.DisposeAsync();
            throw;
        }
    }

    private async Task<ITranscriptionTransport> StartAuthorizedAsync(
        LocalAsrExecutionPreview preview, string language,
        FileStream executableLock, FileStream modelLock, FileStream projectorLock,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (LocalAsrExecutionGuard.ValidateLocalFile(preview.ServerExecutablePath) != preview.ServerExecutablePath ||
            LocalAsrExecutionGuard.ValidateLocalFile(preview.ModelPath) != preview.ModelPath ||
            LocalAsrExecutionGuard.ValidateLocalFile(preview.MultimodalProjectorPath) != preview.MultimodalProjectorPath ||
            !string.Equals(preview.ServerSha256, await ComputeFileHashAsync(executableLock, cancellationToken), StringComparison.Ordinal) ||
            !string.Equals(preview.ModelSha256, await ComputeFileHashAsync(modelLock, cancellationToken), StringComparison.Ordinal) ||
            !string.Equals(preview.MultimodalProjectorSha256, await ComputeFileHashAsync(projectorLock, cancellationToken), StringComparison.Ordinal))
            throw new InvalidOperationException("Los archivos de Qwen3-ASR cambiaron después de la autorización.");
        var verifiedHash = $"SHA256:{preview.ModelSha256};MMPROJ-SHA256:{preview.MultimodalProjectorSha256}";
        var port = ReserveLoopbackPort();
        var apiKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        cancellationToken.ThrowIfCancellationRequested();
        var process = StartServer(preview.ServerExecutablePath, preview.ModelPath,
            preview.MultimodalProjectorPath, port, apiKey);
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectCallback = async (_, token) =>
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(IPAddress.Loopback, port, token);
                    var clientPort = ((IPEndPoint)socket.LocalEndPoint!).Port;
                    var connectionOwned = false;
                    for (var attempt = 0; attempt < 12; attempt++)
                    {
                        if (process.HasExited || !WindowsTcpListenerOwner.IsOwnedBy(port, process.Id))
                            break;
                        if (WindowsTcpListenerOwner.IsEstablishedConnectionOwnedBy(
                                port, clientPort, process.Id))
                        {
                            connectionOwned = true;
                            break;
                        }
                        await Task.Delay(TimeSpan.FromMilliseconds(25), token);
                    }
                    if (!connectionOwned)
                    {
                        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                        catch { }
                        throw new HttpRequestException("La conexión local no pertenece al proceso iniciado.");
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
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}/", UriKind.Absolute),
            Timeout = Timeout.InfiniteTimeSpan
        };
        try
        {
            await WaitUntilReadyAsync(client, process, port, options.StartupTimeout, cancellationToken);
            var readyHash = await ComputeCombinedHashAsync(modelLock, projectorLock, cancellationToken);
            if (!string.Equals(verifiedHash, readyHash, StringComparison.Ordinal))
                throw new InvalidOperationException("Los archivos de Qwen3-ASR cambiaron durante la carga.");
            if (!string.Equals(preview.ServerSha256,
                    await ComputeFileHashAsync(executableLock, cancellationToken), StringComparison.Ordinal))
                throw new InvalidOperationException("El ejecutable Qwen3-ASR cambió durante la carga.");
            return new LlamaCppAsrTransport(
                process, client, port, apiKey, Path.GetFileName(preview.ModelPath),
                NormalizeLanguage(language), verifiedHash);
        }
        catch
        {
            client.Dispose();
            TryKill(process);
            throw;
        }
    }

    private sealed class AuthorizedRun(
        LlamaCppAsrTransportFactory owner, LocalAsrExecutionPreview preview, string language,
        FileStream executableLock, FileStream modelLock, FileStream projectorLock)
        : ITranscriptionExecutionAuthorization
    {
        private int _started;
        private int _disposed;

        public Task<ITranscriptionTransport> StartAsync(CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _started, 1) != 0)
                throw new InvalidOperationException("La autorización de Qwen3-ASR ya fue usada o cerrada.");
            return owner.StartAuthorizedAsync(preview, language, executableLock,
                modelLock, projectorLock, cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            await projectorLock.DisposeAsync();
            await modelLock.DisposeAsync();
            await executableLock.DisposeAsync();
        }
    }

    private Process StartServer(string executablePath, string modelPath, string projectorPath, int port, string apiKey)
    {
        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = false,
            RedirectStandardError = false
        };
        startInfo.Environment["LLAMA_API_KEY"] = apiKey;
        startInfo.ArgumentList.Add("--no-webui");
        startInfo.ArgumentList.Add("-m");
        startInfo.ArgumentList.Add(modelPath);
        startInfo.ArgumentList.Add("--mmproj");
        startInfo.ArgumentList.Add(projectorPath);
        startInfo.ArgumentList.Add("--host");
        startInfo.ArgumentList.Add("127.0.0.1");
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--ctx-size");
        startInfo.ArgumentList.Add(options.ContextSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--n-gpu-layers");
        startInfo.ArgumentList.Add(options.GpuLayers.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("No se pudo iniciar llama-server para Qwen3-ASR.");
    }

    private static async Task WaitUntilReadyAsync(
        HttpClient client,
        Process process,
        int port,
        TimeSpan startupTimeout,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(startupTimeout);
        try
        {
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (process.HasExited)
                    throw new InvalidOperationException("llama-server terminó antes de cargar Qwen3-ASR.");
                try
                {
                    using var response = await client.GetAsync("health", timeout.Token);
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        if (!WindowsTcpListenerOwner.IsOwnedBy(port, process.Id))
                            throw new InvalidOperationException("El puerto local no pertenece a llama-server.");
                        return;
                    }
                }
                catch (HttpRequestException)
                {
                    // The loopback listener may not exist while the model is loading.
                }
                await Task.Delay(TimeSpan.FromMilliseconds(250), timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Se agotó el tiempo para iniciar Qwen3-ASR local.");
        }
    }

    private static int ReserveLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    internal static FileStream OpenModelReadLock(string path) => new(
        path, FileMode.Open, FileAccess.Read, FileShare.Read,
        1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static async Task<string> ComputeCombinedHashAsync(
        FileStream modelLock,
        FileStream projectorLock,
        CancellationToken cancellationToken)
    {
        var modelHash = await ComputeFileHashAsync(modelLock, cancellationToken);
        var projectorHash = await ComputeFileHashAsync(projectorLock, cancellationToken);
        return $"SHA256:{modelHash};MMPROJ-SHA256:{projectorHash}";
    }

    private static async Task<string> ComputeFileHashAsync(
        FileStream stream, CancellationToken cancellationToken)
    {
        stream.Position = 0;
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        try { return Convert.ToHexString(hash); }
        finally { CryptographicOperations.ZeroMemory(hash); }
    }

    private static string NormalizeLanguage(string language) => language.Trim().ToLowerInvariant() switch
    {
        "es" or "spanish" or "español" => "Spanish",
        "en" or "english" or "inglés" => "English",
        _ => string.Empty
    };

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { }
        finally { process.Dispose(); }
    }

    private sealed class LlamaCppAsrTransport(
        Process process,
        HttpClient client,
        int port,
        string apiKey,
        string modelIdentity,
        string language,
        string verifiedModelHash) : ITranscriptionTransport
    {
        private readonly LlamaCppChatAsrClient _asr = new(client, modelIdentity, language, apiKey,
            () =>
            {
                try
                {
                    if (!process.HasExited && WindowsTcpListenerOwner.IsOwnedBy(port, process.Id))
                        return true;
                }
                catch { }
                TryKill(process);
                return false;
            });
        public string? VerifiedModelHash { get; } = verifiedModelHash;

        public Task<WorkerResponse> TranscribeAsync(
            string workId,
            byte[] pcm16,
            CancellationToken cancellationToken) =>
            _asr.TranscribeAsync(workId, pcm16, cancellationToken);

        public Task StopAsync(CancellationToken cancellationToken)
        {
            TryKill(process);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            client.Dispose();
            TryKill(process);
            return ValueTask.CompletedTask;
        }
    }
}

internal sealed class LlamaCppChatAsrClient(
    HttpClient client, string modelIdentity, string language, string apiKey,
    Func<bool> ownsListener, TimeSpan? requestTimeout = null)
{
    private const int MaximumPcmBytes = 16_000 * 2 * 60;
    private const int MaximumResponseBytes = 64 * 1024;
    private const int MaximumTextCharacters = 32_000;
    private static readonly Regex OutputPrefix = new(
        @"^\s*language\s+[^<\r\n]+<asr_text>\s*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public async Task<WorkerResponse> TranscribeAsync(
        string workId,
        byte[] pcm16,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pcm16);
        cancellationToken.ThrowIfCancellationRequested();
        if (pcm16.Length == 0 || pcm16.Length % 2 != 0 || pcm16.Length > MaximumPcmBytes)
            return new(false, "El audio debe ser PCM16 mono, no vacío y de hasta 60 segundos.", workId);
        if (!ownsListener())
            return new(false, "El servidor Qwen3-ASR local ya no es confiable.", workId);
        var wav = WavPcm.CreateMono16(pcm16);
        byte[]? payload = null;
        try
        {
            payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                model = modelIdentity,
                messages = BuildMessages(wav),
                stream = false,
                temperature = 0
            });
            using var request = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions")
            {
                Content = new ByteArrayContent(payload)
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(requestTimeout ?? TimeSpan.FromMinutes(5));
            if (!ownsListener())
                return new(false, "El servidor Qwen3-ASR local ya no es confiable.", workId);
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            if (!response.IsSuccessStatusCode)
                return new(false, $"llama.cpp rechazó la transcripción ({(int)response.StatusCode}).", workId);
            if (response.Content.Headers.ContentLength > MaximumResponseBytes)
                return new(false, "La respuesta de Qwen3-ASR supera el límite permitido.", workId);
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var responseBytes = await ConservativeTranscriptRefinementGenerator.ReadBoundedResponseAsync(
                stream, new byte[4_096], MaximumResponseBytes, timeout.Token);
            try
            {
                using var document = JsonDocument.Parse(responseBytes, new JsonDocumentOptions { MaxDepth = 16 });
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("choices", out var choices) ||
                    choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() != 1 ||
                    choices[0].ValueKind != JsonValueKind.Object ||
                    !choices[0].TryGetProperty("message", out var message) ||
                    message.ValueKind != JsonValueKind.Object ||
                    !message.TryGetProperty("content", out var contentElement) ||
                    contentElement.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("La respuesta de Qwen3-ASR no tiene un formato válido.");
                var content = contentElement.GetString();
                if (content is null || content.Length > MaximumTextCharacters)
                    throw new InvalidDataException("La respuesta de Qwen3-ASR supera el límite permitido.");
                var text = NormalizeOutput(content);
                if (text.Length == 0)
                    return new(false, "Qwen3-ASR no devolvió texto para este fragmento.", workId);
                var durationMilliseconds = checked((long)Math.Ceiling(pcm16.Length / 2d / 16_000d * 1_000d));
                return new(true, WorkId: workId, Segments: [new(0, durationMilliseconds, text)]);
            }
            finally { CryptographicOperations.ZeroMemory(responseBytes); }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, "Se agotó el tiempo para transcribir con Qwen3-ASR local.", workId);
        }
        catch (HttpRequestException)
        {
            return new(false, "No se pudo consultar Qwen3-ASR local.", workId);
        }
        catch (JsonException)
        {
            return new(false, "llama.cpp devolvió una respuesta de transcripción no válida.", workId);
        }
        catch (InvalidDataException)
        {
            return new(false, "llama.cpp devolvió una respuesta de transcripción no válida o demasiado extensa.", workId);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wav);
            if (payload is not null) CryptographicOperations.ZeroMemory(payload);
        }
    }

    private object[] BuildMessages(byte[] wav)
    {
        var messages = new List<object>
        {
            new
            {
                role = "user",
                content = new object[]
                {
                    new
                    {
                        type = "input_audio",
                        input_audio = new { data = wav, format = "wav" }
                    }
                }
            }
        };
        if (language.Length > 0)
            messages.Add(new
            {
                role = "assistant",
                content = $"language {language}<asr_text>"
            });
        return [.. messages];
    }

    internal static string NormalizeOutput(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var normalized = OutputPrefix.Replace(value, string.Empty).Trim();
        return normalized
            .Replace("<|endoftext|>", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();
    }
}

internal readonly record struct WindowsTcpOwnershipRow(
    uint State, uint LocalAddress, ushort LocalPort,
    uint RemoteAddress, ushort RemotePort, int ProcessId);

internal static class WindowsTcpListenerOwner
{
    private const int AddressFamilyInet = 2;
    private const int OwnerPidListenerTable = 3;
    private const int OwnerPidConnectionsTable = 4;
    private const int ErrorInsufficientBuffer = 122;
    private const uint TcpStateListen = 2;
    private const uint TcpStateEstablished = 5;
    private const int MaximumTableBytes = 4 * 1024 * 1024;

    [StructLayout(LayoutKind.Sequential)]
    private struct TcpRow
    {
        public uint State;
        public uint LocalAddress;
        public uint LocalPort;
        public uint RemoteAddress;
        public uint RemotePort;
        public uint OwningPid;
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order,
        int addressFamily, int tableClass, uint reserved);

    public static bool IsOwnedBy(int port, int processId)
    {
        var rows = ReadRows(OwnerPidListenerTable);
        return rows is not null && HasUniqueListenerOwner(rows, port, processId);
    }

    public static bool IsEstablishedConnectionOwnedBy(int serverPort, int clientPort, int processId)
    {
        var rows = ReadRows(OwnerPidConnectionsTable);
        return rows is not null &&
            HasUniqueEstablishedServerOwner(rows, serverPort, clientPort, processId);
    }

    internal static bool HasUniqueListenerOwner(
        IReadOnlyList<WindowsTcpOwnershipRow> rows, int port, int processId)
    {
        if (port is < 1 or > 65_535 || processId <= 0) return false;
        var loopback = BitConverter.ToUInt32(IPAddress.Loopback.GetAddressBytes());
        var matching = rows.Where(row =>
            row.State == TcpStateListen && row.LocalPort == port &&
            (row.LocalAddress == loopback || row.LocalAddress == 0)).ToArray();
        // An additional wildcard or SO_REUSEADDR listener makes routing ambiguous.
        return matching.Length == 1 &&
            matching[0].LocalAddress == loopback &&
            matching[0].ProcessId == processId;
    }

    internal static bool HasUniqueEstablishedServerOwner(
        IReadOnlyList<WindowsTcpOwnershipRow> rows, int serverPort, int clientPort, int processId)
    {
        if (serverPort is < 1 or > 65_535 || clientPort is < 1 or > 65_535 ||
            processId <= 0) return false;
        var loopback = BitConverter.ToUInt32(IPAddress.Loopback.GetAddressBytes());
        var matching = rows.Where(row =>
            row.State == TcpStateEstablished &&
            row.LocalAddress == loopback && row.LocalPort == serverPort &&
            row.RemoteAddress == loopback && row.RemotePort == clientPort).ToArray();
        return matching.Length == 1 && matching[0].ProcessId == processId;
    }

    private static IReadOnlyList<WindowsTcpOwnershipRow>? ReadRows(int tableClass)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var size = 0;
        if (GetExtendedTcpTable(IntPtr.Zero, ref size, false, AddressFamilyInet,
                tableClass, 0) != ErrorInsufficientBuffer ||
            size < sizeof(int) || size > MaximumTableBytes)
            return null;
        var table = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(table, ref size, false, AddressFamilyInet,
                    tableClass, 0) != 0 || size < sizeof(int))
                return null;
            var count = Marshal.ReadInt32(table);
            var rowSize = Marshal.SizeOf<TcpRow>();
            if (count < 0 || count > (size - sizeof(int)) / rowSize)
                return null;
            var rows = new WindowsTcpOwnershipRow[count];
            for (var i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<TcpRow>(
                    IntPtr.Add(table, sizeof(int) + i * rowSize));
                rows[i] = new(
                    row.State, row.LocalAddress, DecodePort(row.LocalPort),
                    row.RemoteAddress, DecodePort(row.RemotePort),
                    unchecked((int)row.OwningPid));
            }
            return rows;
        }
        finally
        {
            Marshal.FreeHGlobal(table);
        }
    }

    private static ushort DecodePort(uint networkOrderPort) => (ushort)
        IPAddress.NetworkToHostOrder(unchecked((short)(networkOrderPort & 0xFFFF)));
}
