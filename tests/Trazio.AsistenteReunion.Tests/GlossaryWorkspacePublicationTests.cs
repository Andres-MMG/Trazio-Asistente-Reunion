using System.Reflection;
using System.Text.Json;
using Trazio.AsistenteReunion.App;

namespace Trazio.AsistenteReunion.Tests;

public sealed class GlossaryWorkspacePublicationTests
{
    [Fact]
    public void GlossaryWorkspace_IsAccessibleHonestAndDoesNotCreateCapability()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "src", "Trazio.AsistenteReunion.App", "MainWindow.xaml"));
        var capabilitiesPath = Path.Combine(root, "src", "Trazio.AsistenteReunion.App", "trazio-capabilities.json");
        using var capabilities = JsonDocument.Parse(File.ReadAllText(capabilitiesPath));

        Assert.Contains("x:Name=\"GlossaryTabItem\" Header=\"Diccionario\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"GlossaryWorkspaceList\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"GlossaryWorkspaceStatusText\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.LiveSetting=\"Polite\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"GlossaryFilterBox\" MaxLength=\"120\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Todos\" Tag=\"All\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Activos\" Tag=\"Active\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Inactivos\" Tag=\"Inactive\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Activa\" IsChecked=\"{Binding IsActive, Mode=OneWay}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Guardar como activa\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Esta versión no modifica Whisper ni reemplaza texto automáticamente", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Usar en futuras transcripciones", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("SourceCorrectionId", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("EntryId", xaml, StringComparison.Ordinal);
        Assert.Contains("entradas del diccionario originadas en sus correcciones", xaml, StringComparison.Ordinal);

        Assert.NotNull(typeof(MainWindow).GetMethod("RefreshGlossary_Click", BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.NotNull(typeof(MainWindow).GetMethod("GlossaryEntryActive_Click", BindingFlags.Instance | BindingFlags.NonPublic));

        var ids = capabilities.RootElement.GetProperty("capabilities")
            .EnumerateArray()
            .Select(item => item.GetProperty("id").GetString())
            .ToArray();
        Assert.Equal(5, ids.Length);
        Assert.DoesNotContain(ids, id => id?.Contains("glossary", StringComparison.OrdinalIgnoreCase) == true);
        Assert.DoesNotContain(ids, id => id?.Contains("dictionary", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public void GlossaryWorkspace_LoadToggleAndCloseUseOneOwnedOperationAndFailClosedPublication()
    {
        var code = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "Trazio.AsistenteReunion.App", "MainWindow.xaml.cs"));

        var toggleStart = code.IndexOf("private async void GlossaryEntryActive_Click", StringComparison.Ordinal);
        var loadStart = code.IndexOf("private async Task LoadGlossaryWorkspaceAsync", toggleStart, StringComparison.Ordinal);
        var clearStart = code.IndexOf("private async Task ClearGlossaryWorkspaceAsync", loadStart, StringComparison.Ordinal);
        var togglePath = code[toggleStart..loadStart];
        var loadPath = code[loadStart..clearStart];

        Assert.Contains("_glossaryWorkspaceOperation.TryBegin", togglePath, StringComparison.Ordinal);
        Assert.Contains("_glossaryWorkspaceOperation.TryBegin", loadPath, StringComparison.Ordinal);
        Assert.Contains("CanPublishGlossaryWorkspace(operation)", togglePath, StringComparison.Ordinal);
        Assert.Contains("CanPublishGlossaryWorkspace(operation)", loadPath, StringComparison.Ordinal);
        Assert.Contains("checkBox.IsChecked = item.IsActive", togglePath, StringComparison.Ordinal);
        Assert.Contains("se restauró el valor anterior", togglePath, StringComparison.Ordinal);
        Assert.Contains("FailGlossaryWorkspace", togglePath, StringComparison.Ordinal);
        Assert.Contains("FailGlossaryWorkspace", loadPath, StringComparison.Ordinal);
        Assert.Contains("_glossaryWorkspaceOperation.Complete(operation)", togglePath, StringComparison.Ordinal);
        Assert.Contains("_glossaryWorkspaceOperation.Complete(operation)", loadPath, StringComparison.Ordinal);

        var selectionStart = code.IndexOf("private async void MainTabs_SelectionChanged", StringComparison.Ordinal);
        var refreshStart = code.IndexOf("private async void RefreshGlossary_Click", selectionStart, StringComparison.Ordinal);
        var selectionPath = code[selectionStart..refreshStart];
        Assert.Contains("e.RemovedItems.Contains(GlossaryTabItem)", selectionPath, StringComparison.Ordinal);
        Assert.Contains("await ClearGlossaryWorkspaceAsync()", selectionPath, StringComparison.Ordinal);

        var failureStart = code.IndexOf("private void FailGlossaryWorkspace", StringComparison.Ordinal);
        var controlsStart = code.IndexOf("private void UpdateGlossaryWorkspaceControls", failureStart, StringComparison.Ordinal);
        var historyRefreshStart = code.IndexOf("private async void RefreshHistory_Click", controlsStart, StringComparison.Ordinal);
        var failurePath = code[failureStart..controlsStart];
        var controlsPath = code[controlsStart..historyRefreshStart];
        Assert.Contains("_glossaryWorkspaceLoadFailed = true", failurePath, StringComparison.Ordinal);
        Assert.Contains("var canBrowse = canInteract && !_glossaryWorkspaceLoadFailed", controlsPath, StringComparison.Ordinal);

        var closeStart = code.IndexOf("private async void Window_Closing", StringComparison.Ordinal);
        var closeEnd = code.IndexOf("private AppSettings ReadSettings", closeStart, StringComparison.Ordinal);
        var closePath = code[closeStart..closeEnd];
        AssertOrdered(closePath,
            "await _glossaryWorkspaceOperation.CancelAndWaitAsync();",
            "_glossaryWorkspaceOperation.Dispose();",
            "_protector?.Dispose();");
    }

    [Fact]
    public void GlossaryWorkspace_FilterIsMemoryOnlyAndNeverUsesSettingsOrSqlite()
    {
        var root = FindRepositoryRoot();
        var code = File.ReadAllText(Path.Combine(root, "src", "Trazio.AsistenteReunion.App", "MainWindow.xaml.cs"));
        var presenter = File.ReadAllText(Path.Combine(root, "src", "Trazio.AsistenteReunion.App", "GlossaryWorkspacePresentation.cs"));

        var filterStart = code.IndexOf("private void ApplyGlossaryWorkspaceFilter", StringComparison.Ordinal);
        var filterEnd = code.IndexOf("private GlossaryEntryActivityFilter SelectedGlossaryActivityFilter", filterStart, StringComparison.Ordinal);
        var filterPath = code[filterStart..filterEnd];
        Assert.Contains("_glossaryWorkspaceEntries", filterPath, StringComparison.Ordinal);
        Assert.Contains("GlossaryWorkspacePresenter.Create", filterPath, StringComparison.Ordinal);
        Assert.DoesNotContain("_store", filterPath, StringComparison.Ordinal);
        Assert.DoesNotContain("_settings", filterPath, StringComparison.Ordinal);
        Assert.DoesNotContain("Sqlite", presenter, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("File.", presenter, StringComparison.Ordinal);
        Assert.DoesNotContain("Http", presenter, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(120, GlossaryWorkspacePresenter.MaximumVisibleItems);
    }

    private static void AssertOrdered(string text, params string[] values)
    {
        var previous = -1;
        foreach (var value in values)
        {
            var current = text.IndexOf(value, previous + 1, StringComparison.Ordinal);
            Assert.True(current > previous, $"No se encontró '{value}' en el orden esperado.");
            previous = current;
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Trazio.AsistenteReunion.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found from the test output directory.");
    }
}
