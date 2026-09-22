using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.Tests;

public sealed class PipelineLogicTests
{
    [Fact]
    public void TryEnqueue_WhenSourceLimitReached_RejectsWithoutDroppingQueuedItem()
    {
        var queue = new PendingAudioQueue(maxMinutesPerSource: 0, maxBytes: 100);
        var chunk = AudioChunk.Create("s", AudioSourceKind.Microphone, 0, DateTimeOffset.UtcNow, new byte[10]);
        Assert.False(queue.TryEnqueue(chunk));
        Assert.Equal(0, queue.PendingBytes);
    }

    [Fact]
    public void TryEnqueue_WhenByteLimitReached_RejectsNewChunk()
    {
        var queue = new PendingAudioQueue(maxMinutesPerSource: 1, maxBytes: 15);
        Assert.True(queue.TryEnqueue(AudioChunk.Create("s", AudioSourceKind.Microphone, 0, DateTimeOffset.UtcNow, new byte[10])));
        Assert.False(queue.TryEnqueue(AudioChunk.Create("s", AudioSourceKind.SystemOutput, 0, DateTimeOffset.UtcNow, new byte[10])));
        Assert.Equal(10, queue.PendingBytes);
    }

    [Fact]
    public void Merge_WithOverlap_RemovesOnlySharedBoundary()
    {
        var merged = TranscriptOverlap.Merge("We need to review the roadmap", "the roadmap tomorrow morning");
        Assert.Equal("We need to review the roadmap tomorrow morning", merged);
    }

    [Fact]
    public void Merge_WithoutOverlap_PreservesBothTexts()
    {
        Assert.Equal("first item second item", TranscriptOverlap.Merge("first item", "second item"));
    }
}
