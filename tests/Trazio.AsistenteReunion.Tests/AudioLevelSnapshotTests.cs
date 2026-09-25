using Trazio.AsistenteReunion.App;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.Tests;

public sealed class AudioLevelSnapshotTests
{
    [Fact]
    public async Task SlowUiReader_DoesNotBlockCaptureLevelPublisherOrGrowQueue()
    {
        var snapshot = new AudioLevelSnapshot();
        using var releaseUi = new ManualResetEventSlim();
        var uiEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockedUi = Task.Run(() =>
        {
            uiEntered.SetResult();
            releaseUi.Wait();
            return snapshot.Read();
        });
        await uiEntered.Task;
        try
        {
            await Task.Run(() =>
            {
                for (var i = 0; i < 10_000; i++)
                    snapshot.Publish(new CapturedSecond(AudioSourceKind.Microphone, [], 0.25f));
                snapshot.Publish(new CapturedSecond(AudioSourceKind.Microphone, [], 0.75f));
                snapshot.Publish(new CapturedSecond(AudioSourceKind.SystemOutput, [], 0.5f));
            }).WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally { releaseUi.Set(); }
        var latest = await blockedUi;
        Assert.Equal(0.75f, latest.Microphone);
        Assert.Equal(0.5f, latest.Output);
    }
}
