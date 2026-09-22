using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.Tests;

public sealed class SingleInstanceGuardTests
{
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
