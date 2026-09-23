using System.Runtime.InteropServices;

namespace Trazio.AsistenteReunion.App;

internal enum VisualCaptureTargetStatus
{
    Available,
    Minimized,
    Lost
}

internal interface IVisualCaptureTargetValidator
{
    VisualCaptureTargetStatus Validate(MeetingWindowSelection selection);
}

internal interface IWindowsGraphicsCaptureFactory
{
    bool IsSupported();
    IVisualCaptureLease CreateForWindow(
        MeetingWindowSelection selection,
        IVisualCaptureTargetValidator targetValidator,
        IVisualProbeFrameConsumer? frameConsumer);
}

internal sealed class WindowsGraphicsCaptureService : IVisualMeetingCapture
{
    private readonly MeetingWindowSelection _selection;
    private readonly IVisualCaptureTargetValidator _targetValidator;
    private readonly IWindowsGraphicsCaptureFactory _factory;
    private readonly IVisualProbeFrameConsumer? _frameConsumer;

    public WindowsGraphicsCaptureService(MeetingWindowSelection selection)
        : this(
            selection,
            new Win32VisualCaptureTargetValidator(new Win32MeetingWindowCatalog()),
            new WindowsGraphicsCaptureFactory(),
            frameConsumer: null)
    {
    }

    internal WindowsGraphicsCaptureService(
        MeetingWindowSelection selection,
        IVisualProbeFrameConsumer? frameConsumer)
        : this(
            selection,
            new Win32VisualCaptureTargetValidator(new Win32MeetingWindowCatalog()),
            new WindowsGraphicsCaptureFactory(),
            frameConsumer)
    {
    }

    internal WindowsGraphicsCaptureService(
        MeetingWindowSelection selection,
        IVisualCaptureTargetValidator targetValidator,
        IWindowsGraphicsCaptureFactory factory)
        : this(selection, targetValidator, factory, frameConsumer: null)
    {
    }

    internal WindowsGraphicsCaptureService(
        MeetingWindowSelection selection,
        IVisualCaptureTargetValidator targetValidator,
        IWindowsGraphicsCaptureFactory factory,
        IVisualProbeFrameConsumer? frameConsumer)
    {
        _selection = selection ?? throw new ArgumentNullException(nameof(selection));
        _targetValidator = targetValidator ?? throw new ArgumentNullException(nameof(targetValidator));
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _frameConsumer = frameConsumer;
    }

    public ValueTask<IVisualCaptureLease> StartValidatedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (!_factory.IsSupported())
                return ValueTask.FromResult<IVisualCaptureLease>(
                    CompletedVisualCaptureLease.For(VisualCaptureExitReason.NotSupported));

            var targetStatus = _targetValidator.Validate(_selection);
            if (targetStatus != VisualCaptureTargetStatus.Available)
                return ValueTask.FromResult<IVisualCaptureLease>(
                    CompletedVisualCaptureLease.For(MapTargetStatus(targetStatus)));

            return ValueTask.FromResult(_factory.CreateForWindow(
                _selection,
                _targetValidator,
                _frameConsumer));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (VisualCaptureStartException exception)
        {
            return ValueTask.FromResult<IVisualCaptureLease>(CompletedVisualCaptureLease.For(exception.Reason));
        }
        catch (PlatformNotSupportedException)
        {
            return ValueTask.FromResult<IVisualCaptureLease>(
                CompletedVisualCaptureLease.For(VisualCaptureExitReason.NotSupported));
        }
        catch
        {
            return ValueTask.FromResult<IVisualCaptureLease>(
                CompletedVisualCaptureLease.For(VisualCaptureExitReason.UnexpectedFailure));
        }
    }

    private static VisualCaptureExitReason MapTargetStatus(VisualCaptureTargetStatus status) => status switch
    {
        VisualCaptureTargetStatus.Minimized => VisualCaptureExitReason.Minimized,
        _ => VisualCaptureExitReason.TargetLost
    };
}

internal sealed class Win32VisualCaptureTargetValidator(IMeetingWindowCatalog catalog)
    : IVisualCaptureTargetValidator
{
    public VisualCaptureTargetStatus Validate(MeetingWindowSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);

        try
        {
            if (!catalog.IsAvailable(selection)) return VisualCaptureTargetStatus.Lost;
            return NativeMethods.IsIconic(selection.Handle)
                ? VisualCaptureTargetStatus.Minimized
                : VisualCaptureTargetStatus.Available;
        }
        catch
        {
            return VisualCaptureTargetStatus.Lost;
        }
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsIconic(nint handle);
    }
}
