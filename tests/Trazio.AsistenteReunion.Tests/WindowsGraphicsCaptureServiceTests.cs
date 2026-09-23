using System.Runtime.InteropServices;
using Trazio.AsistenteReunion.App;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.Tests;

public sealed class WindowsGraphicsCaptureServiceTests
{
    [Fact]
    [Trait("Area", "VisualCapture")]
    public async Task StartValidatedAsync_WhenUnsupported_DoesNotInspectOrCreateTarget()
    {
        var calls = new List<string>();
        var validator = new FakeTargetValidator(VisualCaptureTargetStatus.Available, calls);
        var factory = new FakeCaptureFactory(calls) { Supported = false };
        var service = CreateService(validator, factory);

        await using var lease = await service.StartValidatedAsync(CancellationToken.None);
        var exit = await lease.Completion;

        Assert.Equal(VisualCaptureExitReason.NotSupported, exit.Reason);
        Assert.Equal(["support"], calls);
        Assert.Equal(0, factory.CreateCount);
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public async Task StartValidatedAsync_RevalidatesImmediatelyBeforeFactoryCreation()
    {
        var calls = new List<string>();
        var validator = new FakeTargetValidator(VisualCaptureTargetStatus.Available, calls);
        var expectedLease = new FakeCaptureLease();
        var factory = new FakeCaptureFactory(calls) { Lease = expectedLease };
        var service = CreateService(validator, factory);

        var lease = await service.StartValidatedAsync(CancellationToken.None);

        Assert.Same(expectedLease, lease);
        Assert.Equal(["support", "validate", "create"], calls);
        Assert.Equal(1, validator.ValidateCount);
        Assert.Equal(1, factory.CreateCount);
        await lease.DisposeAsync();
    }

    [Theory]
    [InlineData((int)VisualCaptureTargetStatus.Minimized, VisualCaptureExitReason.Minimized)]
    [InlineData((int)VisualCaptureTargetStatus.Lost, VisualCaptureExitReason.TargetLost)]
    [Trait("Area", "VisualCapture")]
    public async Task StartValidatedAsync_WhenTargetUnavailable_DoesNotCreateCapture(
        int targetStatusValue,
        VisualCaptureExitReason expectedReason)
    {
        var targetStatus = (VisualCaptureTargetStatus)targetStatusValue;
        var calls = new List<string>();
        var validator = new FakeTargetValidator(targetStatus, calls);
        var factory = new FakeCaptureFactory(calls);
        var service = CreateService(validator, factory);

        await using var lease = await service.StartValidatedAsync(CancellationToken.None);
        var exit = await lease.Completion;

        Assert.Equal(expectedReason, exit.Reason);
        Assert.Equal(["support", "validate"], calls);
        Assert.Equal(0, factory.CreateCount);
    }

    [Theory]
    [InlineData(VisualCaptureExitReason.DeviceLost)]
    [InlineData(VisualCaptureExitReason.TargetLost)]
    [Trait("Area", "VisualCapture")]
    public async Task StartValidatedAsync_NormalizesExpectedBackendFailure(VisualCaptureExitReason reason)
    {
        var validator = new FakeTargetValidator(VisualCaptureTargetStatus.Available);
        var factory = new FakeCaptureFactory { Exception = new VisualCaptureStartException(reason) };
        var service = CreateService(validator, factory);

        await using var lease = await service.StartValidatedAsync(CancellationToken.None);
        var exit = await lease.Completion;

        Assert.Equal(reason, exit.Reason);
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public async Task StartValidatedAsync_NormalizesUnexpectedBackendFailure()
    {
        var validator = new FakeTargetValidator(VisualCaptureTargetStatus.Available);
        var factory = new FakeCaptureFactory { Exception = new InvalidOperationException("capture failed") };
        var service = CreateService(validator, factory);

        await using var lease = await service.StartValidatedAsync(CancellationToken.None);
        var exit = await lease.Completion;

        Assert.Equal(VisualCaptureExitReason.UnexpectedFailure, exit.Reason);
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public async Task StartValidatedAsync_WhenCancelled_DoesNotCallPlatform()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var validator = new FakeTargetValidator(VisualCaptureTargetStatus.Available);
        var factory = new FakeCaptureFactory();
        var service = CreateService(validator, factory);

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await service.StartValidatedAsync(cancellation.Token));

        Assert.Equal(0, factory.SupportCount);
        Assert.Equal(0, validator.ValidateCount);
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void TargetValidator_WhenHandleOrProcessNoLongerMatches_ReturnsLost()
    {
        var validator = new Win32VisualCaptureTargetValidator(new FakeWindowCatalog { IsAvailable = false });

        var result = validator.Validate(
            new MeetingWindowSelection((nint)42, 7, MeetingProvider.GoogleMeet));

        Assert.Equal(VisualCaptureTargetStatus.Lost, result);
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void TargetValidator_WhenCatalogThrows_ReturnsLostWithoutEscapingVisualBoundary()
    {
        var validator = new Win32VisualCaptureTargetValidator(new FakeWindowCatalog
        {
            Exception = new InvalidOperationException("window query failed")
        });

        var result = validator.Validate(
            new MeetingWindowSelection((nint)42, 7, MeetingProvider.GoogleMeet));

        Assert.Equal(VisualCaptureTargetStatus.Lost, result);
    }

    [Theory]
    [InlineData((int)VisualCaptureTargetStatus.Minimized, VisualCaptureState.TargetMinimized)]
    [InlineData((int)VisualCaptureTargetStatus.Lost, VisualCaptureState.TargetUnavailable)]
    [Trait("Area", "VisualCapture")]
    public async Task SessionController_MapsUnavailableTargetToNormalizedState(
        int targetStatusValue,
        VisualCaptureState expectedState)
    {
        var targetStatus = (VisualCaptureTargetStatus)targetStatusValue;
        var sessionId = Guid.NewGuid();
        var service = CreateService(
            new FakeTargetValidator(targetStatus),
            new FakeCaptureFactory());
        await using var controller = new VisualCaptureSessionController(sessionId, service);

        var returnedState = await controller.StartAsync(VisualCaptureConsent.GrantForSession(sessionId));

        Assert.Equal(expectedState, returnedState);
        Assert.Equal(expectedState, controller.State);
    }

    [Theory]
    [InlineData(unchecked((int)0x80070006), VisualCaptureExitReason.TargetLost)]
    [InlineData(unchecked((int)0x80070578), VisualCaptureExitReason.TargetLost)]
    [InlineData(unchecked((int)0x80004005), VisualCaptureExitReason.UnexpectedFailure)]
    [Trait("Area", "VisualCapture")]
    public void FailureClassifier_CreateForWindow_MapsOnlyKnownTargetFailures(
        int hResult,
        VisualCaptureExitReason expectedReason)
    {
        var result = VisualCaptureFailureClassifier.ForCreateForWindow(new COMException("capture", hResult));

        Assert.Equal(expectedReason, result);
    }

    [Theory]
    [InlineData(unchecked((int)0x887A0005), VisualCaptureExitReason.DeviceLost)]
    [InlineData(unchecked((int)0x887A0007), VisualCaptureExitReason.DeviceLost)]
    [InlineData(unchecked((int)0x80000013), VisualCaptureExitReason.TargetLost)]
    [InlineData(unchecked((int)0x80004005), VisualCaptureExitReason.UnexpectedFailure)]
    [Trait("Area", "VisualCapture")]
    public void FailureClassifier_DeviceOperation_PreservesDiagnosticBoundary(
        int hResult,
        VisualCaptureExitReason expectedReason)
    {
        var result = VisualCaptureFailureClassifier.ForDeviceOperation(new COMException("capture", hResult));

        Assert.Equal(expectedReason, result);
    }

    private static WindowsGraphicsCaptureService CreateService(
        IVisualCaptureTargetValidator validator,
        IWindowsGraphicsCaptureFactory factory) =>
        new(new MeetingWindowSelection((nint)42, 7, MeetingProvider.GoogleMeet), validator, factory);

    private sealed class FakeTargetValidator(
        VisualCaptureTargetStatus status,
        List<string>? calls = null) : IVisualCaptureTargetValidator
    {
        public int ValidateCount { get; private set; }

        public VisualCaptureTargetStatus Validate(MeetingWindowSelection selection)
        {
            ValidateCount++;
            calls?.Add("validate");
            return status;
        }
    }

    private sealed class FakeCaptureFactory : IWindowsGraphicsCaptureFactory
    {
        private readonly List<string>? _calls;

        public FakeCaptureFactory(List<string>? calls = null) => _calls = calls;

        public bool Supported { get; init; } = true;
        public Exception? Exception { get; init; }
        public IVisualCaptureLease Lease { get; init; } = new FakeCaptureLease();
        public int SupportCount { get; private set; }
        public int CreateCount { get; private set; }

        public bool IsSupported()
        {
            SupportCount++;
            _calls?.Add("support");
            return Supported;
        }

        public IVisualCaptureLease CreateForWindow(
            MeetingWindowSelection selection,
            IVisualCaptureTargetValidator targetValidator)
        {
            CreateCount++;
            _calls?.Add("create");
            if (Exception is not null) throw Exception;
            return Lease;
        }
    }

    private sealed class FakeCaptureLease : IVisualCaptureLease
    {
        private readonly TaskCompletionSource<VisualCaptureExit> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<VisualCaptureExit> Completion => _completion.Task;

        public ValueTask DisposeAsync()
        {
            _completion.TrySetResult(new(VisualCaptureExitReason.Completed));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeWindowCatalog : IMeetingWindowCatalog
    {
        public bool IsAvailable { get; init; }
        public Exception? Exception { get; init; }

        public IReadOnlyList<MeetingWindowCandidate> Enumerate() => [];

        bool IMeetingWindowCatalog.IsAvailable(MeetingWindowSelection selection)
        {
            if (Exception is not null) throw Exception;
            return IsAvailable;
        }
    }
}
