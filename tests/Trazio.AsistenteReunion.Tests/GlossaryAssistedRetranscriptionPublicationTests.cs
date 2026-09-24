namespace Trazio.AsistenteReunion.Tests;

public sealed class GlossaryAssistedRetranscriptionPublicationTests
{
    [Fact]
    public void HistoryRetranscription_RequiresExplicitSafeChoiceBeforeUsingGlossary()
    {
        var code = File.ReadAllText(ProjectPath(
            "src",
            "Trazio.AsistenteReunion.App",
            "MainWindow.xaml.cs"));
        var start = code.IndexOf(
            "private async void Retranscribe_Click",
            StringComparison.Ordinal);
        var end = code.IndexOf(
            "private void CancelRetranscription_Click",
            start,
            StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var path = code[start..end];

        Assert.Contains("GlossaryPromptPlanner.Create", path, StringComparison.Ordinal);
        Assert.Contains("ConfirmGlossaryPrompt", path, StringComparison.Ordinal);
        Assert.Contains("MessageBoxButton.YesNoCancel", path, StringComparison.Ordinal);
        Assert.Contains("MessageBoxResult.Cancel", path, StringComparison.Ordinal);
        Assert.Contains("MessageBoxResult.No => GlossaryPromptPlan.None", path, StringComparison.Ordinal);
        Assert.Contains("Esto no reemplaza palabras automáticamente ni cambia el original", path, StringComparison.Ordinal);
        Assert.Contains("confirmedPlan", path, StringComparison.Ordinal);
        Assert.DoesNotContain("SaveCorrection", path, StringComparison.Ordinal);
        Assert.DoesNotContain("AddGlossary", path, StringComparison.Ordinal);
    }

    [Fact]
    public void Worker_AppliesOnlyBoundedPromptAtModelStartup()
    {
        var worker = File.ReadAllText(ProjectPath(
            "src",
            "Trazio.AsistenteReunion.Worker",
            "Program.cs"));
        var transport = File.ReadAllText(ProjectPath(
            "src",
            "Trazio.AsistenteReunion.App",
            "TranscriptionTransport.cs"));

        Assert.Contains("GlossaryPromptPlanner.MaximumPromptCharacters", worker, StringComparison.Ordinal);
        Assert.Contains("request.InitialPrompt?.Any(char.IsControl)", worker, StringComparison.Ordinal);
        Assert.Contains("builder.WithPrompt(request.InitialPrompt)", worker, StringComparison.Ordinal);
        Assert.Contains("new(\"start\", modelPath, language, InitialPrompt: initialPrompt)", transport, StringComparison.Ordinal);
        Assert.DoesNotContain("InitialPrompt:", ExtractTranscribeTransportPath(transport), StringComparison.Ordinal);
    }

    [Fact]
    public void RevisionPresentation_DeclaresWhetherConfirmedGlossaryWasUsed()
    {
        var presentation = File.ReadAllText(ProjectPath(
            "src",
            "Trazio.AsistenteReunion.App",
            "TranscriptPresentation.cs"));
        var domain = File.ReadAllText(ProjectPath(
            "src",
            "Trazio.AsistenteReunion.Core",
            "ModelRevisionDomain.cs"));

        Assert.Contains("GlossaryPromptVersion", domain, StringComparison.Ordinal);
        Assert.Contains("GlossaryPromptPlan.NoGlossaryVersion", presentation, StringComparison.Ordinal);
        Assert.Contains("diccionario confirmado", presentation, StringComparison.Ordinal);
    }

    private static string ExtractTranscribeTransportPath(string transport)
    {
        var start = transport.IndexOf(
            "public Task<WorkerResponse> TranscribeAsync",
            StringComparison.Ordinal);
        Assert.True(start >= 0);
        return transport[start..];
    }

    private static string ProjectPath(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Trazio.AsistenteReunion.slnx")))
                return Path.Combine([directory.FullName, .. parts]);
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found from the test output directory.");
    }
}
