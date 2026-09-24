using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.Tests;

public sealed class SingleInstanceGuardTests
{
    [Fact]
    public void ApplicationRunningMarker_UsesInstallerMutexAndKeepsItOpenUntilDisposed()
    {
        Assert.Equal("Trazio.AsistenteReunion.AppRunning.v1", ApplicationRunningMarker.MutexName);
        var testName = $"Trazio.AsistenteReunion.Tests.{Guid.NewGuid():N}.running";

        using (var marker = ApplicationRunningMarker.CreateNamed(testName))
        {
            Assert.True(Mutex.TryOpenExisting(testName, out var observed));
            observed!.Dispose();
        }

        Assert.False(Mutex.TryOpenExisting(testName, out var afterDispose));
        afterDispose?.Dispose();
    }

    [Fact]
    public void TryAcquire_ConcurrentSecondFailsWithoutCreatingStorage_ThenSucceedsAfterDispose()
    {
        var firstRoot = Path.Combine(Path.GetTempPath(), "trazio-instance-a-" + Guid.NewGuid().ToString("N"));
        var secondRoot = Path.Combine(Path.GetTempPath(), "trazio-instance-b-" + Guid.NewGuid().ToString("N"));

        using var first = SingleInstanceGuard.TryAcquire(firstRoot);
        Assert.NotNull(first);

        using var second = SingleInstanceGuard.TryAcquire(secondRoot);

        Assert.Null(second);
        Assert.False(Directory.Exists(firstRoot));
        Assert.False(Directory.Exists(secondRoot));

        first.Dispose();
        using var afterDispose = SingleInstanceGuard.TryAcquire(secondRoot);
        Assert.NotNull(afterDispose);
    }
}
