using System.Reflection;
using System.Text.Json;
using Trazio.AsistenteReunion.App;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.Tests;

public sealed class VersionMetadataTests
{
    [Fact]
    public void Assemblies_UseBetaVersionAndProductName()
    {
        var assemblies = new[] { typeof(MainWindow).Assembly, typeof(MeetingSession).Assembly };
        foreach (var assembly in assemblies)
        {
            Assert.Equal(new Version(0, 2, 0, 0), assembly.GetName().Version);
            Assert.Equal("Trazio Asistente Reunión", assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product);
            Assert.Equal("0.2.0.0", assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version);
            Assert.StartsWith("0.2.0-beta.1", assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
        }
    }

    [Fact]
    public void CapabilityManifest_DeclaresPackagedObsidianExportImplementedByApplication()
    {
        var manifestPath = Path.Combine(AppContext.BaseDirectory, "trazio-capabilities.json");

        Assert.True(File.Exists(manifestPath), $"Capability manifest not copied to output: {manifestPath}");
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("Trazio Asistente Reunión", root.GetProperty("product").GetString());
        Assert.Contains(
            root.GetProperty("capabilities").EnumerateArray(),
            capability => capability.GetProperty("id").GetString() == "obsidian-markdown-export"
                && capability.GetProperty("version").GetInt32() == 1);
        Assert.Contains(
            root.GetProperty("capabilities").EnumerateArray(),
            capability => capability.GetProperty("id").GetString() == "meeting-window-provider-association-v1"
                && capability.GetProperty("version").GetInt32() == 1);
        Assert.NotNull(typeof(MainWindow).GetMethod("SelectMeetingWindow_Click", BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.NotNull(typeof(MainWindow).GetMethod("ClearMeetingWindow_Click", BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.NotNull(typeof(MeetingWindowClassifier).GetMethod(nameof(MeetingWindowClassifier.Classify)));
        Assert.NotNull(typeof(MainWindow).GetMethod("ExportObsidian_Click", BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.NotNull(typeof(ObsidianMarkdownExport).GetMethod(nameof(ObsidianMarkdownExport.Create)));
    }
}
