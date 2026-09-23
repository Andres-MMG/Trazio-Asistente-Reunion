namespace Trazio.AsistenteReunion.App;

internal static class VisualCaptureShutdown
{
    public static async Task RunVisualFirstAsync(
        Func<Task> stopVisual,
        Func<Task> stopRemaining)
    {
        ArgumentNullException.ThrowIfNull(stopVisual);
        ArgumentNullException.ThrowIfNull(stopRemaining);

        try
        {
            await stopVisual();
        }
        catch
        {
            // Visual teardown never prevents audio/transcription shutdown.
        }

        await stopRemaining();
    }
}
