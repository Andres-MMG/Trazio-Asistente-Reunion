using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.Tests;

public sealed class GlossaryCandidateExtractorTests
{
    [Fact]
    public void Extract_ReturnsEveryChangedWordWithoutCopyingTheWholePhrase()
    {
        var result = GlossaryCandidateExtractor.Extract(
            "y claro tuve que tener una reunión no más, pues ya sea Need o NTeams ¿cierto?",
            "y claro tuve que tener una reunión no más, pues ya sea Meet o Teams ¿cierto?");

        Assert.Equal(
            [new GlossaryCandidate("Need", "Meet"), new GlossaryCandidate("NTeams", "Teams")],
            result);
    }

    [Theory]
    [InlineData("same words", "same words")]
    [InlineData("same words.", "same words!")]
    [InlineData("Meet", "meet")]
    public void Extract_IgnoresTextWithoutAWordReplacement(string original, string corrected)
    {
        Assert.Empty(GlossaryCandidateExtractor.Extract(original, corrected));
    }

    [Fact]
    public void Extract_IgnoresPureInsertionsAndDeletions()
    {
        Assert.Empty(GlossaryCandidateExtractor.Extract("alpha beta", "alpha new beta"));
        Assert.Empty(GlossaryCandidateExtractor.Extract("alpha old beta", "alpha beta"));
    }

    [Fact]
    public void Extract_DeduplicatesRepeatedReplacementPairs()
    {
        var result = GlossaryCandidateExtractor.Extract("Need and Need", "Meet and Meet");

        Assert.Equal([new GlossaryCandidate("Need", "Meet")], result);
    }
}
