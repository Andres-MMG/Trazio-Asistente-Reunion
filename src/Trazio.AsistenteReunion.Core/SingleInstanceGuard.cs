using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace Trazio.AsistenteReunion.Core;

public sealed class SingleInstanceGuard : IDisposable
{
    private NamedPipeServerStream? _server;

    private SingleInstanceGuard(NamedPipeServerStream server) => _server = server;

    public static string PipeName
    {
        get
        {
            var identity = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
            var userHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16];
            return $"Trazio.AsistenteReunion.{userHash}.instance.v1";
        }
    }

    public static SingleInstanceGuard? TryAcquire(string? ignoredDataDirectory = null) =>
        TryAcquireNamed(PipeName);

    internal static SingleInstanceGuard? TryAcquireNamed(string pipeName)
    {
        try
        {
            var server = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            return new(server);
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void Dispose() => Interlocked.Exchange(ref _server, null)?.Dispose();
}
