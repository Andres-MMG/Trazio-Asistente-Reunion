using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.System.WinRT.Graphics.Capture;

namespace Trazio.AsistenteReunion.App;

internal sealed class WindowsGraphicsCaptureFactory : IWindowsGraphicsCaptureFactory
{
    private static readonly Guid GraphicsCaptureItemInterfaceId =
        new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    public bool IsSupported() => GraphicsCaptureSession.IsSupported();

    public IVisualCaptureLease CreateForWindow(
        MeetingWindowSelection selection,
        IVisualCaptureTargetValidator targetValidator)
    {
        using var activationFactory =
            WinRT.ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        var interop = activationFactory.AsInterface<IGraphicsCaptureItemInterop>();
        GraphicsCaptureItem item;
        try
        {
            var targetStatus = targetValidator.Validate(selection);
            if (targetStatus != VisualCaptureTargetStatus.Available)
                throw new VisualCaptureStartException(MapTargetStatus(targetStatus));

            // The target was revalidated immediately before this HWND-bound call.
            object rawItem;
            unsafe
            {
                var interfaceId = GraphicsCaptureItemInterfaceId;
                interop.CreateForWindow(new HWND(selection.Handle), &interfaceId, out rawItem);
            }

            var rawItemPointer = Marshal.GetIUnknownForObject(rawItem);
            try
            {
                item = WinRT.MarshalInspectable<GraphicsCaptureItem>.FromAbi(rawItemPointer);
            }
            finally
            {
                Marshal.Release(rawItemPointer);
                ReleaseComObject(rawItem);
            }
        }
        catch (VisualCaptureStartException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new VisualCaptureStartException(
                VisualCaptureFailureClassifier.ForCreateForWindow(exception),
                exception);
        }
        finally
        {
            ReleaseComObject(interop);
        }

        try
        {
            return WindowsGraphicsCaptureLease.Start(item, selection, targetValidator);
        }
        catch (VisualCaptureStartException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new VisualCaptureStartException(
                VisualCaptureFailureClassifier.ForDeviceOperation(exception),
                exception);
        }
    }

    private static void ReleaseComObject(object value)
    {
        try
        {
            if (Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
        }
        catch
        {
        }
    }

    private static VisualCaptureExitReason MapTargetStatus(VisualCaptureTargetStatus status) => status switch
    {
        VisualCaptureTargetStatus.Minimized => VisualCaptureExitReason.Minimized,
        _ => VisualCaptureExitReason.TargetLost
    };
}

internal sealed class WindowsGraphicsCaptureLease : IVisualCaptureLease
{
    private const int BufferCount = 2;
    private static readonly DirectXPixelFormat PixelFormat = DirectXPixelFormat.B8G8R8A8UIntNormalized;
    private static readonly TimeSpan TargetPollInterval = TimeSpan.FromMilliseconds(500);

    private readonly object _gate = new();
    private readonly MeetingWindowSelection _selection;
    private readonly IVisualCaptureTargetValidator _targetValidator;
    private readonly TaskCompletionSource<VisualCaptureExit> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _targetMonitorCancellation = new();
    private readonly D3D11CaptureDevice _device;
    private readonly GraphicsCaptureItem _item;
    private readonly Direct3D11CaptureFramePool _framePool;
    private readonly GraphicsCaptureSession _session;
    private Task _targetMonitor = Task.CompletedTask;
    private Task? _disposeTask;
    private SizeInt32 _frameSize;
    private bool _disposed;

    private WindowsGraphicsCaptureLease(
        D3D11CaptureDevice device,
        GraphicsCaptureItem item,
        Direct3D11CaptureFramePool framePool,
        GraphicsCaptureSession session,
        MeetingWindowSelection selection,
        IVisualCaptureTargetValidator targetValidator,
        SizeInt32 initialSize)
    {
        _device = device;
        _item = item;
        _framePool = framePool;
        _session = session;
        _selection = selection;
        _targetValidator = targetValidator;
        _frameSize = initialSize;

        _item.Closed += OnItemClosed;
        _framePool.FrameArrived += OnFrameArrived;
    }

    public Task<VisualCaptureExit> Completion => _completion.Task;

    public static WindowsGraphicsCaptureLease Start(
        GraphicsCaptureItem item,
        MeetingWindowSelection selection,
        IVisualCaptureTargetValidator targetValidator)
    {
        var initialSize = item.Size;
        if (initialSize.Width <= 0 || initialSize.Height <= 0)
            throw new VisualCaptureStartException(VisualCaptureExitReason.Minimized);

        var device = D3D11CaptureDevice.Create();
        Direct3D11CaptureFramePool? framePool = null;
        GraphicsCaptureSession? session = null;
        WindowsGraphicsCaptureLease? lease = null;

        try
        {
            framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                device.WinRtDevice,
                PixelFormat,
                BufferCount,
                initialSize);
            session = framePool.CreateCaptureSession(item);
            session.IsCursorCaptureEnabled = false;
            lease = new(device, item, framePool, session, selection, targetValidator, initialSize);
            session.StartCapture();
            lease.StartTargetMonitor();
            return lease;
        }
        catch
        {
            if (lease is not null)
                lease.DisposeAsync().AsTask().GetAwaiter().GetResult();
            else
            {
                DisposeSafely(session);
                DisposeSafely(framePool);
                DisposeSafely(device);
            }
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource disposeCompletion;
        Task targetMonitor;
        lock (_gate)
        {
            if (_disposeTask is not null) return new(_disposeTask);

            _disposed = true;
            targetMonitor = _targetMonitor;
            disposeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposeTask = disposeCompletion.Task;
        }

        _ = FinishDisposeAsync(targetMonitor, disposeCompletion);
        return new(_disposeTask);
    }

    private async Task FinishDisposeAsync(Task targetMonitor, TaskCompletionSource disposeCompletion)
    {
        try
        {
            TryDetachEvents();
            TryCancelTargetMonitor();
            DisposeSafely(_session);
            DisposeSafely(_framePool);
            DisposeSafely(_device);
            Complete(VisualCaptureExitReason.Completed);
            await targetMonitor.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected when the lease owns the monitor shutdown.
        }
        finally
        {
            _targetMonitorCancellation.Dispose();
            disposeCompletion.TrySetResult();
        }
    }

    private void StartTargetMonitor()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _targetMonitor = MonitorTargetAsync(_targetMonitorCancellation.Token);
        }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        lock (_gate)
        {
            if (_disposed) return;

            try
            {
                SizeInt32 contentSize;
                using (var frame = sender.TryGetNextFrame())
                {
                    contentSize = frame.ContentSize;
                }

                if (contentSize.Width <= 0 || contentSize.Height <= 0)
                {
                    Complete(VisualCaptureExitReason.Minimized);
                    return;
                }

                if (contentSize.Width == _frameSize.Width && contentSize.Height == _frameSize.Height) return;

                sender.Recreate(_device.WinRtDevice, PixelFormat, BufferCount, contentSize);
                _frameSize = contentSize;
            }
            catch (Exception exception)
            {
                // Device recovery is deliberately deferred until it can be exercised with a physical harness.
                Complete(VisualCaptureFailureClassifier.ForFrame(exception));
            }
        }
    }

    private void OnItemClosed(GraphicsCaptureItem sender, object args) =>
        Complete(VisualCaptureExitReason.TargetLost);

    private async Task MonitorTargetAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TargetPollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var status = _targetValidator.Validate(_selection);
                if (status == VisualCaptureTargetStatus.Available) continue;

                Complete(status == VisualCaptureTargetStatus.Minimized
                    ? VisualCaptureExitReason.Minimized
                    : VisualCaptureExitReason.TargetLost);
                return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            Complete(VisualCaptureExitReason.UnexpectedFailure);
        }
    }

    private void Complete(VisualCaptureExitReason reason) =>
        _completion.TrySetResult(new(reason));

    private void TryDetachEvents()
    {
        try
        {
            _item.Closed -= OnItemClosed;
        }
        catch
        {
        }

        try
        {
            _framePool.FrameArrived -= OnFrameArrived;
        }
        catch
        {
        }
    }

    private void TryCancelTargetMonitor()
    {
        try
        {
            _targetMonitorCancellation.Cancel();
        }
        catch
        {
        }
    }

    private static void DisposeSafely(IDisposable? resource)
    {
        try
        {
            resource?.Dispose();
        }
        catch
        {
            // Visual cleanup is isolated from audio and transcription.
        }
    }
}

internal sealed class D3D11CaptureDevice : IDisposable
{
    private readonly ID3D11Device _nativeDevice;
    private readonly ID3D11DeviceContext _nativeContext;
    private int _disposed;

    private D3D11CaptureDevice(
        ID3D11Device nativeDevice,
        ID3D11DeviceContext nativeContext,
        IDirect3DDevice winRtDevice)
    {
        _nativeDevice = nativeDevice;
        _nativeContext = nativeContext;
        WinRtDevice = winRtDevice;
    }

    public IDirect3DDevice WinRtDevice { get; }

    public static D3D11CaptureDevice Create()
    {
        var result = PInvoke.D3D11CreateDevice(
            default!,
            D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE,
            default,
            D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            ReadOnlySpan<D3D_FEATURE_LEVEL>.Empty,
            PInvoke.D3D11_SDK_VERSION,
            out var nativeDevice,
            out var nativeContext);
        result.ThrowOnFailure();

        Windows.Win32.System.WinRT.IInspectable? inspectable = null;
        try
        {
            var dxgiDevice = (IDXGIDevice)nativeDevice;
            PInvoke.CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out inspectable).ThrowOnFailure();

            var inspectablePointer = Marshal.GetIUnknownForObject(inspectable);
            try
            {
                var winRtDevice = WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(inspectablePointer);
                return new(nativeDevice, nativeContext, winRtDevice);
            }
            finally
            {
                Marshal.Release(inspectablePointer);
            }
        }
        catch
        {
            ReleaseComObject(nativeContext);
            ReleaseComObject(nativeDevice);
            throw;
        }
        finally
        {
            if (inspectable is not null) ReleaseComObject(inspectable);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        try
        {
            WinRtDevice.Dispose();
        }
        catch
        {
        }

        ReleaseComObject(_nativeContext);
        ReleaseComObject(_nativeDevice);
    }

    private static void ReleaseComObject(object value)
    {
        try
        {
            if (Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
        }
        catch
        {
        }
    }
}

internal sealed class CompletedVisualCaptureLease : IVisualCaptureLease
{
    private CompletedVisualCaptureLease(VisualCaptureExitReason reason) =>
        Completion = Task.FromResult(new VisualCaptureExit(reason));

    public Task<VisualCaptureExit> Completion { get; }

    public static CompletedVisualCaptureLease For(VisualCaptureExitReason reason) => new(reason);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class VisualCaptureStartException : Exception
{
    public VisualCaptureStartException(VisualCaptureExitReason reason)
        : base($"Visual capture could not start: {reason}.") => Reason = reason;

    public VisualCaptureStartException(VisualCaptureExitReason reason, Exception innerException)
        : base($"Visual capture could not start: {reason}.", innerException) => Reason = reason;

    public VisualCaptureExitReason Reason { get; }
}

internal static class VisualCaptureFailureClassifier
{
    private const int RoClosed = unchecked((int)0x80000013);
    private const int RpcDisconnected = unchecked((int)0x80010108);
    private const int InvalidHandle = unchecked((int)0x80070006);
    private const int InvalidArgument = unchecked((int)0x80070057);
    private const int InvalidWindowHandle = unchecked((int)0x80070578);
    private const int DxgiDeviceRemoved = unchecked((int)0x887A0005);
    private const int DxgiDeviceHung = unchecked((int)0x887A0006);
    private const int DxgiDeviceReset = unchecked((int)0x887A0007);
    private const int DxgiDriverInternalError = unchecked((int)0x887A0020);

    public static VisualCaptureExitReason ForCreateForWindow(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is PlatformNotSupportedException) return VisualCaptureExitReason.NotSupported;
        return IsLostTarget(exception.HResult)
            ? VisualCaptureExitReason.TargetLost
            : VisualCaptureExitReason.UnexpectedFailure;
    }

    public static VisualCaptureExitReason ForDeviceOperation(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is PlatformNotSupportedException) return VisualCaptureExitReason.NotSupported;
        if (IsDeviceLost(exception.HResult)) return VisualCaptureExitReason.DeviceLost;
        return IsClosed(exception.HResult)
            ? VisualCaptureExitReason.TargetLost
            : VisualCaptureExitReason.UnexpectedFailure;
    }

    public static VisualCaptureExitReason ForFrame(Exception exception) =>
        ForDeviceOperation(exception);

    private static bool IsLostTarget(int hResult) =>
        hResult is InvalidHandle or InvalidArgument or InvalidWindowHandle || IsClosed(hResult);

    private static bool IsClosed(int hResult) => hResult is RoClosed or RpcDisconnected;

    private static bool IsDeviceLost(int hResult) =>
        hResult is DxgiDeviceRemoved or DxgiDeviceHung or DxgiDeviceReset or DxgiDriverInternalError;
}
