using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.Tests;

public sealed class TranscriptContextPlannerTests
{
    [Fact]
    public void Build_KeepsNearestBoundedSegmentsAndDeduplicatedGlossary()
    {
        var timeline = Enumerable.Range(0, 10)
            .Select(index => new TranscriptContextItem(
                TimeSpan.FromSeconds(index * 10),
                TimeSpan.FromSeconds(index * 10 + 5),
                $"segmento {index}",
                HumanApproved: index < 5));

        var context = TranscriptContextPlanner.Build(
            timeline,
            TimeSpan.FromSeconds(50),
            TimeSpan.FromSeconds(55),
            ["Trazio", "trazio", "Meet"],
            maximumSegmentsPerSide: 2,
            maximumCharacters: 256);

        Assert.Equal(["segmento 3", "segmento 4"], context.Previous.Select(item => item.Text));
        Assert.Equal(["segmento 6", "segmento 7"], context.Following.Select(item => item.Text));
        Assert.Equal(["Meet", "Trazio"], context.GlossaryTerms);
    }
}
