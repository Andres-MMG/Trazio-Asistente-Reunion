using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.Tests;

public sealed class TranscriptRefinementPolicyTests
{
    [Fact]
    public void ValidateDrafts_EmptyResult_PreservesNoChange()
    {
        Assert.Empty(TranscriptRefinementPolicy.ValidateDrafts("frase original", []));
    }

    [Theory]
    [InlineData("Frase  original", " frase original ")]
    [InlineData("frase original", "FRASE ORIGINAL")]
    public void ValidateDrafts_DuplicateOfOriginal_IsRejected(string original, string candidate)
    {
        Assert.Throws<ArgumentException>(() => TranscriptRefinementPolicy.ValidateDrafts(
            original, [new(candidate)]));
    }

    [Fact]
    public void ValidateDrafts_SecondAlternativeRequiresAmbiguityMarker()
    {
        Assert.Throws<ArgumentException>(() => TranscriptRefinementPolicy.ValidateDrafts(
            "original", [new("primera"), new("segunda")]));
        var accepted = TranscriptRefinementPolicy.ValidateDrafts(
            "original", [new("primera"), new("segunda", true)]);
        Assert.Equal(2, accepted.Count);
        Assert.True(accepted[1].IsAmbiguousAlternative);
    }

    [Fact]
    public void ValidateDrafts_DuplicateAlternativesAndOversizedText_AreRejected()
    {
        Assert.Throws<ArgumentException>(() => TranscriptRefinementPolicy.ValidateDrafts(
            "original", [new("alternativa"), new(" ALTERNATIVA ", true)]));
        Assert.Throws<ArgumentException>(() => TranscriptRefinementPolicy.ValidateDrafts(
            "original", [new(new string('x', TranscriptRefinementPolicy.MaxTextCharacters + 1))]));
        Assert.Throws<ArgumentException>(() => TranscriptRefinementPolicy.ValidateDrafts(
            "original", [new("uno"), new("dos", true), new("tres", true)]));
    }
}
