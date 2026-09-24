using System.Reflection;
using System.Text.Json;
using Trazio.AsistenteReunion.App;

namespace Trazio.AsistenteReunion.Tests;

public sealed class HistorySearchPublicationTests
{
    [Fact]
    public void HistorySearch_IsAccessibleLocalOnlyAndDoesNotCreateCapabilityOrAutoplayPath()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "src", "Trazio.AsistenteReunion.App", "MainWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(root, "src", "Trazio.AsistenteReunion.App", "MainWindow.xaml.cs"));
        var capabilitiesPath = Path.Combine(root, "src", "Trazio.AsistenteReunion.App", "trazio-capabilities.json");
        using var capabilities = JsonDocument.Parse(File.ReadAllText(capabilitiesPath));

        Assert.Contains("x:Name=\"HistorySearchBox\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Buscar en reuniones\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"HistorySearchStatusText\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.LiveSetting=\"Polite\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"HistorySearchResults\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Resultados de búsqueda en reuniones", xaml, StringComparison.Ordinal);
        Assert.NotNull(typeof(MainWindow).GetMethod("SearchHistory_Click", BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.NotNull(typeof(MainWindow).GetMethod("ClearHistorySearch_Click", BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.NotNull(typeof(MainWindow).GetMethod("HistorySearchResults_SelectionChanged", BindingFlags.Instance | BindingFlags.NonPublic));

        var selectionStart = code.IndexOf("private async void HistorySearchResults_SelectionChanged", StringComparison.Ordinal);
        var selectionEnd = code.IndexOf("private void UpdateSessionTitleEditor", selectionStart, StringComparison.Ordinal);
        var selectionPath = code[selectionStart..selectionEnd];
        Assert.DoesNotContain("PlayFromPositionAsync", selectionPath, StringComparison.Ordinal);
        Assert.DoesNotContain("PlaySelectedSegmentAsync", selectionPath, StringComparison.Ordinal);
        Assert.Contains("HistorySearchNavigationIntent.From", selectionPath, StringComparison.Ordinal);

        var ids = capabilities.RootElement.GetProperty("capabilities")
            .EnumerateArray()
            .Select(item => item.GetProperty("id").GetString())
            .ToArray();
        Assert.Equal(5, ids.Length);
        Assert.Contains("history-review-workspace", ids);
        Assert.DoesNotContain(ids, id => id?.Contains("search", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public void HistorySearch_LifecycleInvalidatesAndDrainsBeforeDeleteOrKeyDisposal()
    {
        var root = FindRepositoryRoot();
        var code = File.ReadAllText(Path.Combine(root, "src", "Trazio.AsistenteReunion.App", "MainWindow.xaml.cs"));

        var deleteStart = code.IndexOf("private async void Delete_Click", StringComparison.Ordinal);
        var deleteEnd = code.IndexOf("private async void ChangeStorageFolder_Click", deleteStart, StringComparison.Ordinal);
        var deletePath = code[deleteStart..deleteEnd];
        AssertOrdered(deletePath,
            "_historySearch.Invalidate();",
            "_historySearchNavigation.Invalidate();",
            "HideHistorySearchResults();",
            "await _historySearchActivity.BlockAndDrainAsync();",
            "await _audioArchive.DeleteSessionAsync(session.Id)");

        var closeStart = code.IndexOf("private async void Window_Closing", StringComparison.Ordinal);
        var closeEnd = code.IndexOf("private AppSettings ReadSettings", closeStart, StringComparison.Ordinal);
        var closePath = code[closeStart..closeEnd];
        AssertOrdered(closePath,
            "_historySearchNavigation.Invalidate();",
            "await _historySearchActivity.BlockAndDrainAsync();",
            "_protector?.Dispose();");

        var selectedSessionStart = code.IndexOf("private async Task SelectHistorySessionAsync", StringComparison.Ordinal);
        var selectionStart = code.IndexOf("private async void HistorySearchResults_SelectionChanged", selectedSessionStart, StringComparison.Ordinal);
        var selectedSessionPath = code[selectedSessionStart..selectionStart];
        AssertAwaitImmediatelyRevalidated(
            selectedSessionPath,
            "await SupersedePlaybackAsync()",
            "if (navigationTicket is not null && !IsCurrentHistorySearchNavigation(navigationTicket)) return;");
        AssertAwaitImmediatelyRevalidated(
            selectedSessionPath,
            "await LoadHistoryReviewAsync(",
            "if (navigationTicket is not null && !IsCurrentHistorySearchNavigation(navigationTicket)) return;");

        var selectionEnd = code.IndexOf("private void UpdateSessionTitleEditor", selectionStart, StringComparison.Ordinal);
        var selectionPath = code[selectionStart..selectionEnd];
        Assert.Contains("_historySearchNavigation.Begin", selectionPath, StringComparison.Ordinal);
        AssertOrdered(selectionPath,
            "_historySearchActivity.TryBegin",
            "using (lease)",
            "await OpenHistorySearchResultAsync(intent, ticket)");
        AssertAwaitImmediatelyRevalidated(
            selectionPath,
            "await _store.ListSessionsAsync(ticket.CancellationToken)",
            "if (!IsCurrentHistorySearchNavigation(ticket)) return;");
        AssertAwaitImmediatelyRevalidated(
            selectionPath,
            "await RefreshHistoryAsync(ticket.CancellationToken, suppressSelectionChanged: true)",
            "if (!IsCurrentHistorySearchNavigation(ticket)) return;");
        AssertAwaitImmediatelyRevalidated(
            selectionPath,
            "await SelectHistorySessionAsync(storedSession, intent, ticket)",
            "if (!IsCurrentHistorySearchNavigation(ticket)) return;");
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

    private static void AssertAwaitImmediatelyRevalidated(
        string text,
        string awaitMarker,
        string revalidation)
    {
        var awaitStart = text.IndexOf(awaitMarker, StringComparison.Ordinal);
        Assert.True(awaitStart >= 0, $"No se encontró la espera crítica '{awaitMarker}'.");
        var awaitEnd = text.IndexOf(';', awaitStart);
        Assert.True(awaitEnd >= awaitStart, $"La espera crítica '{awaitMarker}' no termina correctamente.");
        var validationStart = text.IndexOf(revalidation, awaitEnd + 1, StringComparison.Ordinal);
        Assert.True(validationStart > awaitEnd, $"No se encontró la revalidación posterior a '{awaitMarker}'.");
        Assert.True(
            string.IsNullOrWhiteSpace(text[(awaitEnd + 1)..validationStart]),
            $"La revalidación debe ejecutarse inmediatamente después de '{awaitMarker}'.");
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
