using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Trazio.AsistenteReunion.App;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.Tests;

public sealed class VersionMetadataTests
{
    private const string ExpectedVersion = "0.2.0-beta.4";
    private const string ExpectedReleaseArchive = "Trazio-Asistente-Reunion-v0.2.0-beta.4-win-x64.zip";

    [Fact]
    public void Assemblies_UseBetaVersionAndProductName()
    {
        var assemblies = new[]
        {
            typeof(MainWindow).Assembly,
            typeof(MeetingSession).Assembly,
            typeof(AnonymousVisualActivityCorrelator).Assembly
        };
        foreach (var assembly in assemblies)
        {
            Assert.Equal(new Version(0, 2, 0, 0), assembly.GetName().Version);
            Assert.Equal("Trazio Asistente Reunión", assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product);
            Assert.Equal("0.2.0.0", assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version);
            Assert.StartsWith(ExpectedVersion, assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
        }
    }

    [Fact]
    public void ReleaseBuild_DisablesDebugSymbolsAndCodeView()
    {
        var root = FindRepositoryRoot();
        var document = XDocument.Load(Path.Combine(root, "Directory.Build.props"));
        var releaseProperties = Assert.Single(
            document.Root!.Elements("PropertyGroup"),
            group => string.Equals(
                group.Attribute("Condition")?.Value,
                "'$(Configuration)' == 'Release'",
                StringComparison.Ordinal));

        Assert.Equal("none", releaseProperties.Element("DebugType")?.Value);
        Assert.Equal("false", releaseProperties.Element("DebugSymbols")?.Value);
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void CapabilityManifest_DeclaresPackagedCapabilitiesImplementedByApplication()
    {
        var manifestPath = Path.Combine(AppContext.BaseDirectory, "trazio-capabilities.json");

        Assert.True(File.Exists(manifestPath), $"Capability manifest not copied to output: {manifestPath}");
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("Trazio Asistente Reunión", root.GetProperty("product").GetString());
        var capabilities = root.GetProperty("capabilities").EnumerateArray().ToArray();
        Assert.All(capabilities, capability =>
        {
            Assert.False(string.IsNullOrWhiteSpace(capability.GetProperty("id").GetString()));
            Assert.True(capability.GetProperty("version").GetInt32() > 0);
        });
        Assert.Equal(
            capabilities.Length,
            capabilities.Select(capability => capability.GetProperty("id").GetString()).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(5, capabilities.Length);
        AssertCapability(capabilities, "history-review-workspace", 1);
        AssertCapability(capabilities, "encrypted-audio-retention", 1);
        AssertCapability(capabilities, "obsidian-markdown-export", 1);
        AssertCapability(capabilities, "meeting-window-provider-association-v1", 1);
        AssertCapability(capabilities, "consented-ephemeral-window-capture-v1", 1);
        Assert.NotNull(typeof(MainWindow).GetMethod("SelectMeetingWindow_Click", BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.NotNull(typeof(MainWindow).GetMethod("ClearMeetingWindow_Click", BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.NotNull(typeof(MainWindow).GetMethod("AuthorizeVisualCapture_Click", BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.NotNull(typeof(MainWindow).GetMethod("PauseVisualCapture_Click", BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.NotNull(typeof(MainWindow).GetMethod("ResumeVisualCapture_Click", BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.NotNull(typeof(MainWindow).GetMethod("StopVisualCapture_Click", BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.Contains(typeof(IVisualMeetingCapture), typeof(WindowsGraphicsCaptureService).GetInterfaces());
        Assert.NotNull(typeof(MeetingWindowClassifier).GetMethod(nameof(MeetingWindowClassifier.Classify)));
        Assert.NotNull(typeof(MainWindow).GetMethod("ExportObsidian_Click", BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.NotNull(typeof(ObsidianMarkdownExport).GetMethod(nameof(ObsidianMarkdownExport.Create)));
    }

    private static void AssertCapability(JsonElement[] capabilities, string id, int version)
    {
        var match = Assert.Single(
            capabilities,
            capability => string.Equals(capability.GetProperty("id").GetString(), id, StringComparison.Ordinal));
        Assert.Equal(version, match.GetProperty("version").GetInt32());
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void PublishScript_RequiresReleaseContractAndRejectsUnsafeLayout()
    {
        var root = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(root, "installer", "publish.ps1"));

        var versionMatch = Regex.Match(script, "\\$expectedVersion\\s*=\\s*\"([^\"]+)\"");
        var archiveTemplateMatch = Regex.Match(script, "\\$expectedArchiveName\\s*=\\s*\"([^\"]+)\"");
        Assert.True(versionMatch.Success, "publish.ps1 must declare the expected release version.");
        Assert.True(archiveTemplateMatch.Success, "publish.ps1 must declare the expected archive name.");
        Assert.Equal(ExpectedVersion, versionMatch.Groups[1].Value);
        Assert.Equal(
            ExpectedReleaseArchive,
            archiveTemplateMatch.Groups[1].Value.Replace("$expectedVersion", versionMatch.Groups[1].Value, StringComparison.Ordinal));
        Assert.Contains("$expectedChecksumName = \"$expectedArchiveName.sha256\"", script, StringComparison.Ordinal);
        Assert.Contains("consented-ephemeral-window-capture-v1", script, StringComparison.Ordinal);
        Assert.Contains("$capabilities.Count -ne $requiredCapabilities.Count", script, StringComparison.Ordinal);
        Assert.Contains("must declare exactly", script, StringComparison.Ordinal);
        Assert.Contains("HashSet[string]", script, StringComparison.Ordinal);
        Assert.Contains("StringComparer]::Ordinal", script, StringComparison.Ordinal);
        Assert.Contains("$match.Count -ne 1", script, StringComparison.Ordinal);
        Assert.Contains("$allowedPublishExtensions", script, StringComparison.Ordinal);
        Assert.Contains("$allowedMetadataFiles", script, StringComparison.Ordinal);
        Assert.Contains("$allowedExecutableFiles", script, StringComparison.Ordinal);
        Assert.Contains("\"createdump.exe\"", script, StringComparison.Ordinal);
        Assert.Contains("\".png\"", script, StringComparison.Ordinal);
        Assert.Contains("\".mp4\"", script, StringComparison.Ordinal);
        Assert.Contains("\".dmp\"", script, StringComparison.Ordinal);
        Assert.Contains("\".log\"", script, StringComparison.Ordinal);
        Assert.Contains("\"ggml-\"", script, StringComparison.Ordinal);
        Assert.Contains("Published layout contains private or unsupported retained artifacts", script, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "installer", "publish.ps1")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found from the test output directory.");
    }
}
