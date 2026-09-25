namespace Trazio.AsistenteReunion.Tests;

public sealed class ExternalAiProviderPublicationTests
{
    [Fact]
    public void IntelligenceTab_ExplainsThatSavingDoesNotSendMeetingData()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "src", "Trazio.AsistenteReunion.App", "MainWindow.xaml"));

        Assert.Contains("Header=\"Inteligencia\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Guardar esta configuración no envía transcripciones", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ExternalAiApiKeyBox\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"{Binding ApiKey", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderSettingsUi_OffersExplicitSaveAndSecretRemoval()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "src", "Trazio.AsistenteReunion.App", "MainWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(root, "src", "Trazio.AsistenteReunion.App", "MainWindow.xaml.cs"));

        Assert.Contains("SaveExternalAiSettings_Click", xaml, StringComparison.Ordinal);
        Assert.Contains("DeleteExternalAiApiKey_Click", xaml, StringComparison.Ordinal);
        Assert.Contains("ExternalAiProviderPolicy.ResolveApiKey", code, StringComparison.Ordinal);
        Assert.Contains("ExternalAiProviderPolicy.Create", code, StringComparison.Ordinal);
        Assert.Contains("_settings with", code, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
