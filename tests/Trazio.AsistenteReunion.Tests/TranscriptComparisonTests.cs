using Trazio.AsistenteReunion.App;

namespace Trazio.AsistenteReunion.Tests;

public sealed class TranscriptComparisonTests
{
    [Fact]
    public void Align_GroupsBothVersionsByTheSameSessionTimeIntervals()
    {
        var first = new[]
        {
            new ComparisonTextSegment(TimeSpan.FromSeconds(16), TimeSpan.FromSeconds(19), "segundo tramo"),
            new ComparisonTextSegment(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4), "primera"),
            new ComparisonTextSegment(TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(12), "parte")
        };
        var second = new[]
        {
            new ComparisonTextSegment(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(7), "texto nuevo"),
            new ComparisonTextSegment(TimeSpan.FromSeconds(31), TimeSpan.FromSeconds(33), "solo segundo modelo")
        };

        var rows = TranscriptComparison.Align(first, second, TimeSpan.FromSeconds(15));

        Assert.Equal(3, rows.Count);
        Assert.Equal(TimeSpan.Zero, rows[0].Start);
        Assert.Equal("primera parte", rows[0].LeftText);
        Assert.Equal("texto nuevo", rows[0].RightText);
        Assert.Equal(TimeSpan.FromSeconds(15), rows[1].Start);
        Assert.Equal("segundo tramo", rows[1].LeftText);
        Assert.Equal(string.Empty, rows[1].RightText);
        Assert.Equal(TimeSpan.FromSeconds(30), rows[2].Start);
        Assert.Equal("solo segundo modelo", rows[2].RightText);
    }

    [Fact]
    public void Align_RejectsInvalidIntervalAndIgnoresBlankText()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TranscriptComparison.Align([], [], TimeSpan.Zero));

        var rows = TranscriptComparison.Align(
            [new ComparisonTextSegment(TimeSpan.Zero, TimeSpan.FromSeconds(2), "  ")],
            [],
            TimeSpan.FromSeconds(15));

        Assert.Empty(rows);
    }
}
