using Trazio.AsistenteReunion.App;

namespace Trazio.AsistenteReunion.Tests;

public sealed class RefinementWordDiffTests
{
    [Fact]
    public void Compare_HighlightsChangedNamesAndNegation()
    {
        var result = RefinementWordDiff.Compare("Need no confirmó.", "Meet sí confirmó.");
        Assert.Equal([true, true, false], result.Original.Select(token => token.IsChanged));
        Assert.Equal([true, true, false], result.Proposal.Select(token => token.IsChanged));
    }

    [Fact]
    public void Compare_PreservesUnchangedContext()
    {
        var result = RefinementWordDiff.Compare("Hoy usamos Need en Chile", "Hoy usamos Meet en Chile");
        Assert.Equal([false, false, true, false, false], result.Original.Select(token => token.IsChanged));
        Assert.Equal([false, false, true, false, false], result.Proposal.Select(token => token.IsChanged));
    }

    [Fact]
    public void Compare_HandlesLongInputWithoutQuadraticMatrix()
    {
        var prefix = string.Join(' ', Enumerable.Repeat("contexto", 260));
        var result = RefinementWordDiff.Compare($"{prefix} Need final", $"{prefix} Meet final");
        Assert.Equal(262, result.Original.Count);
        Assert.True(result.Original[260].IsChanged);
        Assert.True(result.Proposal[260].IsChanged);
        Assert.False(result.Proposal[261].IsChanged);
    }

    [Fact]
    public void Compare_NegationChange_WarnsOnDeletedWordWithoutClaimingCertainty()
    {
        var result = RefinementWordDiff.Compare("No confirmamos", "Sí confirmamos");
        Assert.Equal(RefinementSensitiveChangeKind.Negation, result.Original[0].SensitiveKind);
        Assert.Equal(RefinementSensitiveChangeKind.None, result.Proposal[0].SensitiveKind);
        Assert.Contains("posibles cambios en negaciones", result.SensitiveChangeSummary);
        Assert.Contains("verificar con audio", result.Original[0].AccessibleDescription);
    }

    [Fact]
    public void Compare_NumberAndDateChanges_WarnOnBothSides()
    {
        var result = RefinementWordDiff.Compare("Monto 15 fecha 21/09/2026", "Monto 50 fecha 22/09/2026");
        Assert.Equal(RefinementSensitiveChangeKind.Number, result.Original[1].SensitiveKind);
        Assert.Equal(RefinementSensitiveChangeKind.Number, result.Proposal[1].SensitiveKind);
        Assert.Equal(RefinementSensitiveChangeKind.Date, result.Original[3].SensitiveKind);
        Assert.Equal(RefinementSensitiveChangeKind.Date, result.Proposal[3].SensitiveKind);
        Assert.Contains("fechas", result.SensitiveChangeSummary);
        Assert.Contains("cifras", result.SensitiveChangeSummary);
    }

    [Fact]
    public void Compare_PossibleNames_AreLabelledAsHeuristic()
    {
        var result = RefinementWordDiff.Compare("Usamos Need hoy", "Usamos Meet hoy");
        Assert.Equal(RefinementSensitiveChangeKind.PossibleName, result.Original[1].SensitiveKind);
        Assert.Equal(RefinementSensitiveChangeKind.PossibleName, result.Proposal[1].SensitiveKind);
        Assert.Contains("posible cambio de nombre", result.Original[1].AccessibleDescription);
        Assert.Contains("heurística", result.SensitiveChangeSummary);
    }

    [Fact]
    public void Compare_AccentNegationAndPunctuationOnly_AvoidFalseWarning()
    {
        var negation = RefinementWordDiff.Compare("No, jamás", "Sí, siempre");
        Assert.Equal(RefinementSensitiveChangeKind.Negation, negation.Original[0].SensitiveKind);
        Assert.Equal(RefinementSensitiveChangeKind.Negation, negation.Original[1].SensitiveKind);

        var punctuation = RefinementWordDiff.Compare("Hola, no.", "Hola no");
        Assert.All(punctuation.Original.Concat(punctuation.Proposal), token =>
            Assert.Equal(RefinementSensitiveChangeKind.None, token.SensitiveKind));
        Assert.False(punctuation.HasSensitiveChanges);
    }

    [Fact]
    public void Compare_LongDiffFallback_StillWarnsOnChangedName()
    {
        var prefix = string.Join(' ', Enumerable.Repeat("contexto", 260));
        var result = RefinementWordDiff.Compare($"{prefix} Need final", $"{prefix} Meet final");
        Assert.Equal(RefinementSensitiveChangeKind.PossibleName, result.Original[260].SensitiveKind);
        Assert.Equal(RefinementSensitiveChangeKind.PossibleName, result.Proposal[260].SensitiveKind);
    }

    [Fact]
    public void ComparisonUi_ExposesWarningTextAndNonColorTokenCue()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Trazio.AsistenteReunion.slnx")))
            root = root.Parent;
        Assert.NotNull(root);

        var xaml = File.ReadAllText(Path.Combine(root!.FullName,
            "src", "Trazio.AsistenteReunion.App", "MainWindow.xaml"));
        Assert.Contains("x:Name=\"RefinementSensitiveWarningText\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.LiveSetting=\"Polite\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Binding AccessibleDescription", xaml, StringComparison.Ordinal);
        Assert.Contains("Binding IsSensitive", xaml, StringComparison.Ordinal);
        Assert.Contains("TextDecorations\" Value=\"Underline\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"{Binding Text, RelativeSource={RelativeSource Self}}\"", xaml, StringComparison.Ordinal);
        var code = File.ReadAllText(Path.Combine(root.FullName,
            "src", "Trazio.AsistenteReunion.App", "MainWindow.Refinement.cs"));
        Assert.Contains("RaiseAutomationEvent(AutomationEvents.LiveRegionChanged)", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Compare_ChangedOrdinaryWords_AnnounceWhichSideChanged()
    {
        var result = RefinementWordDiff.Compare("El proyecto avanza", "El proyecto mejora");
        Assert.Contains("Original: avanza; eliminado o sustituido", result.Original[2].AccessibleDescription);
        Assert.Contains("Propuesta: mejora; añadido o distinto", result.Proposal[2].AccessibleDescription);
        Assert.Contains("sin cambios", result.Original[1].AccessibleDescription);
    }

    [Theory]
    [InlineData("$50.000", "$60.000")]
    [InlineData("CLP$50.000", "CLP$60.000")]
    [InlineData("US$50.000", "US$60.000")]
    [InlineData("€50,00", "€60,00")]
    public void Compare_CurrencyAmounts_ArePossibleFigures(string originalAmount, string proposalAmount)
    {
        var result = RefinementWordDiff.Compare($"Total {originalAmount}", $"Total {proposalAmount}");
        Assert.Equal(RefinementSensitiveChangeKind.Number, result.Original[1].SensitiveKind);
        Assert.Equal(RefinementSensitiveChangeKind.Number, result.Proposal[1].SensitiveKind);
        Assert.Contains("posible cambio de cifra", result.Proposal[1].AccessibleDescription);
    }

    [Fact]
    public void Compare_CurrencyCodeAlone_IsNotAFigure()
    {
        var result = RefinementWordDiff.Compare("Pago CLP", "Pago USD");
        Assert.False(result.HasSensitiveChanges);
    }
}
