using System.Text.RegularExpressions;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.Tests;

public sealed class InstallerPackageContractTests
{
    [Fact]
    public void InnoSetup_UsesPerUserVersionedFailSafeContract()
    {
        var root = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(root, "installer", "Trazio.AsistenteReunion.iss"));

        Assert.Contains("AppId={{{#MyAppGuid}}", script, StringComparison.Ordinal);
        Assert.Contains("DefaultDirName={#MyDefaultDirName}", script, StringComparison.Ordinal);
        Assert.Contains(@"DestDir: ""{app}\versions\{#MyAppVersion}""", script, StringComparison.Ordinal);
        Assert.Contains(@"Filename: ""{app}\versions\{#MyAppVersion}\{#MyAppExeName}""", script, StringComparison.Ordinal);
        Assert.Contains("PrivilegesRequired=lowest", script, StringComparison.Ordinal);
        Assert.Contains("CloseApplications=no", script, StringComparison.Ordinal);
        Assert.Contains("RestartApplications=no", script, StringComparison.Ordinal);
        Assert.Contains("RestartIfNeededByRun=no", script, StringComparison.Ordinal);
        Assert.Contains($"#define MyAppMutex \"{ApplicationRunningMarker.MutexName}\"", script, StringComparison.Ordinal);
        Assert.Contains("AppMutex={#MyAppMutex}", script, StringComparison.Ordinal);
        Assert.Contains("#define MySetupMutex \"Trazio.AsistenteReunion.Setup.v1\"", script, StringComparison.Ordinal);
        Assert.Contains("SetupMutex={#MySetupMutex}", script, StringComparison.Ordinal);
        Assert.Contains("UsePreviousAppDir=yes", script, StringComparison.Ordinal);
        Assert.Contains("UninstallFilesDir={app}", script, StringComparison.Ordinal);
        Assert.Contains("UninstallLogMode=append", script, StringComparison.Ordinal);
        Assert.Contains("#ifndef MyShortcutName", script, StringComparison.Ordinal);
        Assert.Contains("#error MyShortcutName must be defined by build-installer.ps1", script, StringComparison.Ordinal);
        Assert.Contains(@"Name: ""{autodesktop}\{#MyShortcutName}""", script, StringComparison.Ordinal);
        Assert.DoesNotContain(@"Name: ""{autodesktop}\{#MyAppName}""", script, StringComparison.Ordinal);
        Assert.Contains("Name: \"spanish\"; MessagesFile: \"compiler:Languages\\Spanish.isl\"", script, StringComparison.Ordinal);
        Assert.Contains("SuppressibleMsgBox", script, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex(@"(?<!Suppressible)MsgBox\(", RegexOptions.CultureInvariant), script);
        Assert.DoesNotContain("[InstallDelete]", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("[UninstallDelete]", script, StringComparison.OrdinalIgnoreCase);

        var filesSection = GetSection(script, "Files");
        var fileDestinations = Regex.Matches(
            filesSection,
            @"^\s*Source:.*?;\s*DestDir:\s*""(?<destination>[^""]+)""",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);
        Assert.NotEmpty(fileDestinations);
        Assert.All(
            fileDestinations.Cast<Match>(),
            match => Assert.Equal(@"{app}\versions\{#MyAppVersion}", match.Groups["destination"].Value));

        var registrySection = GetSection(script, "Registry");
        var registryEntries = registrySection.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.NotEmpty(registryEntries);
        Assert.All(registryEntries, entry => Assert.Contains("{#MyInstallerRegistrySubkey}", entry, StringComparison.Ordinal));

        Assert.DoesNotContain(@"{localappdata}\Trazio Asistente Reunion", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("trazio-transcripts.db", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("master.key", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("settings.dat", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("StorageStateV1", script, StringComparison.Ordinal);
        Assert.DoesNotContain("ActiveRoot", script, StringComparison.Ordinal);
        Assert.DoesNotContain("PendingSource", script, StringComparison.Ordinal);
        Assert.DoesNotContain("PendingTarget", script, StringComparison.Ordinal);
        Assert.DoesNotContain("CleanupPendingSource", script, StringComparison.Ordinal);
        Assert.DoesNotContain("DelTree(", script, StringComparison.Ordinal);
        Assert.DoesNotContain("DeleteFile(", script, StringComparison.Ordinal);
        Assert.DoesNotContain("RemoveDir(", script, StringComparison.Ordinal);
        Assert.DoesNotContain("RenameFile(", script, StringComparison.Ordinal);
        Assert.DoesNotContain("FileCopy(", script, StringComparison.Ordinal);
        Assert.DoesNotContain("SaveStringToFile(", script, StringComparison.Ordinal);
    }

    [Fact]
    public void InnoSetup_BindsSequenceAndRejectsUnknownLegacyOrDowngrade()
    {
        var root = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(root, "installer", "Trazio.AsistenteReunion.iss"));

        Assert.Contains("CurrentReleaseSequence = {#MyReleaseSequence};", script, StringComparison.Ordinal);
        Assert.Contains("RegKeyExists(HKCU, InstallerRegistrySubkey)", script, StringComparison.Ordinal);
        Assert.Contains("RegQueryStringValue(HKCU, InstallerRegistrySubkey, 'Version'", script, StringComparison.Ordinal);
        Assert.Contains("RegQueryDWordValue", script, StringComparison.Ordinal);
        Assert.Contains("StoredSequence < 1", script, StringComparison.Ordinal);
        Assert.Contains("not TryMapKnownVersion(InstalledVersion, MappedSequence)", script, StringComparison.Ordinal);
        Assert.Contains("MappedSequence <> InstalledSequence", script, StringComparison.Ordinal);
        Assert.Contains("else if TryReadLegacyVersion(InstalledVersion)", script, StringComparison.Ordinal);
        Assert.Contains("InstalledSequence > CurrentReleaseSequence", script, StringComparison.Ordinal);
        Assert.Contains("VersionText = '0.1.1-mvp'", script, StringComparison.Ordinal);
        Assert.Contains("VersionText = '0.2.0-beta.5'", script, StringComparison.Ordinal);
        Assert.Contains("VersionText = '0.2.0-beta.6'", script, StringComparison.Ordinal);
        Assert.Contains("Sequence := 6", script, StringComparison.Ordinal);
        Assert.Contains("Sequence := 7", script, StringComparison.Ordinal);
        Assert.Contains("DetectedInstallMode := 'repair'", script, StringComparison.Ordinal);
        Assert.Contains("DetectedInstallMode := 'upgrade'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("TryMapLegacyVersion", script, StringComparison.Ordinal);
        Assert.DoesNotContain("CompareText", script, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallerScripts_ValidatePayloadAndUseDisposableHarnessIdentifiers()
    {
        var root = FindRepositoryRoot();
        var publishScript = File.ReadAllText(Path.Combine(root, "installer", "publish.ps1"));
        var buildScript = File.ReadAllText(Path.Combine(root, "installer", "build-installer.ps1"));
        var harnessScript = File.ReadAllText(Path.Combine(root, "installer", "test-installer.ps1"));
        AssertUtf8Bom(Path.Combine(root, "installer", "publish.ps1"));
        AssertUtf8Bom(Path.Combine(root, "installer", "build-installer.ps1"));
        AssertUtf8Bom(Path.Combine(root, "installer", "test-installer.ps1"));

        Assert.Contains(@"Programs\Inno Setup 6\ISCC.exe", buildScript, StringComparison.Ordinal);
        Assert.Contains("Assert-PayloadManifest", buildScript, StringComparison.Ordinal);
        Assert.Contains("Get-AuthenticodeSignature", buildScript, StringComparison.Ordinal);
        Assert.Contains("authenticode = \"unsigned\"", buildScript, StringComparison.Ordinal);
        Assert.Contains(".sha256", buildScript, StringComparison.Ordinal);
        Assert.Contains(".manifest.json", buildScript, StringComparison.Ordinal);
        Assert.Contains("AllowTestOverrides", buildScript, StringComparison.Ordinal);
        Assert.Contains("$explicitParameterNames", buildScript, StringComparison.Ordinal);
        Assert.Contains("TestWorkspaceRoot", buildScript, StringComparison.Ordinal);
        Assert.Contains("ShortcutName", buildScript, StringComparison.Ordinal);
        Assert.Contains("$ShortcutName -ceq $productShortcutName", buildScript, StringComparison.Ordinal);
        Assert.Contains("Test-OverlappingIdentity $ShortcutName $productShortcutName", buildScript, StringComparison.Ordinal);
        Assert.Contains("ShortcutNameRunId", buildScript, StringComparison.Ordinal);
        Assert.Contains("(?<runId>[0-9a-f]{32})$", buildScript, StringComparison.Ordinal);
        Assert.Contains("/DMyShortcutName=$ShortcutName", buildScript, StringComparison.Ordinal);
        Assert.Contains("$Version -ceq $canonicalVersion", buildScript, StringComparison.Ordinal);
        Assert.Contains("$ReleaseSequence -eq $canonicalReleaseSequence", buildScript, StringComparison.Ordinal);
        Assert.Contains("publish.ps1", buildScript, StringComparison.Ordinal);
        Assert.Contains("Assert-InstallerArtifacts", buildScript, StringComparison.Ordinal);
        Assert.Contains("todos los parámetros explícitos de identidad, versión, payload, salida y TestWorkspaceRoot", buildScript, StringComparison.Ordinal);
        Assert.Contains("PayloadDirectory -AllowedRoot $TestWorkspaceRoot", buildScript, StringComparison.Ordinal);
        Assert.Contains("PayloadManifestPath -AllowedRoot $TestWorkspaceRoot", buildScript, StringComparison.Ordinal);
        Assert.Contains("Get-NormalizedRelativePath", buildScript, StringComparison.Ordinal);
        Assert.Contains("Get-NormalizedRelativePath", publishScript, StringComparison.Ordinal);
        Assert.Contains("Read-Utf8Text", publishScript, StringComparison.Ordinal);
        Assert.Contains("Read-Utf8Text", buildScript, StringComparison.Ordinal);
        Assert.Contains("Read-Utf8Text", harnessScript, StringComparison.Ordinal);
        Assert.DoesNotContain("[System.IO.Path]::GetRelativePath", buildScript, StringComparison.Ordinal);
        Assert.DoesNotContain("[System.IO.Path]::GetRelativePath", publishScript, StringComparison.Ordinal);
        Assert.DoesNotContain("[System.IO.Path]::GetRelativePath", harnessScript, StringComparison.Ordinal);
        Assert.DoesNotContain("Get-Content -LiteralPath", publishScript, StringComparison.Ordinal);
        Assert.DoesNotContain("Get-Content -LiteralPath", buildScript, StringComparison.Ordinal);
        Assert.DoesNotContain("Get-Content -LiteralPath", harnessScript, StringComparison.Ordinal);

        Assert.Contains("[Guid]::NewGuid()", harnessScript, StringComparison.Ordinal);
        Assert.Contains("InstallerHarness", harnessScript, StringComparison.Ordinal);
        Assert.Contains("trazio-installer-harness-", harnessScript, StringComparison.Ordinal);
        Assert.Contains("downgrade-b-to-a", harnessScript, StringComparison.Ordinal);
        Assert.Contains("modern-sequence-zero", harnessScript, StringComparison.Ordinal);
        Assert.Contains("modern-version-sequence-mismatch", harnessScript, StringComparison.Ordinal);
        Assert.Contains("modern-state-missing-sequence", harnessScript, StringComparison.Ordinal);
        Assert.Contains("modern-state-missing-version", harnessScript, StringComparison.Ordinal);
        Assert.Contains("running-app-block", harnessScript, StringComparison.Ordinal);
        Assert.Contains("-not $mutexProcess.HasExited", harnessScript, StringComparison.Ordinal);
        Assert.Contains("transactional-rollback", harnessScript, StringComparison.Ordinal);
        Assert.Contains("[System.IO.FileShare]::None", harnessScript, StringComparison.Ordinal);
        Assert.Contains("$rollbackExitCode -eq 5", harnessScript, StringComparison.Ordinal);
        Assert.Contains("unknown-legacy-rejected", harnessScript, StringComparison.Ordinal);
        Assert.Contains("$productOverrideCases", harnessScript, StringComparison.Ordinal);
        Assert.Contains("$productIdentityCases", harnessScript, StringComparison.Ordinal);
        Assert.Contains("$partialDisposableRejected", harnessScript, StringComparison.Ordinal);
        Assert.Contains("TestWorkspaceRoot = $workspace", harnessScript, StringComparison.Ordinal);
        Assert.Contains("ShortcutName = $shortcutName", harnessScript, StringComparison.Ordinal);
        Assert.Contains("$shortcutName.Contains($runId)", harnessScript, StringComparison.Ordinal);
        Assert.Contains("Assert-ProductDesktopShortcutPreserved", harnessScript, StringComparison.Ordinal);
        Assert.Contains("$desktopShortcutPath", harnessScript, StringComparison.Ordinal);
        Assert.Contains("/MERGETASKS=desktopicon", harnessScript, StringComparison.Ordinal);
        Assert.Contains("archivos arbitrarios fuera de su árbol", harnessScript, StringComparison.Ordinal);
        Assert.Contains("interactive-cancellation", harnessScript, StringComparison.Ordinal);
        Assert.Contains("NOT_AUTOMATED", harnessScript, StringComparison.Ordinal);
        Assert.Contains("El harness no puede usar el AppId productivo.", harnessScript, StringComparison.Ordinal);
        Assert.DoesNotContain("InjectTransactionalFailure", buildScript, StringComparison.Ordinal);
        Assert.DoesNotContain("MyHarnessFailure", buildScript, StringComparison.Ordinal);
        Assert.DoesNotContain("MyHarnessFailure", File.ReadAllText(Path.Combine(root, "installer", "Trazio.AsistenteReunion.iss")), StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "installer", "Trazio.AsistenteReunion.iss")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found from the test output directory.");
    }

    private static string GetSection(string script, string sectionName)
    {
        var match = Regex.Match(
            script,
            $@"(?ms)^\[{Regex.Escape(sectionName)}\]\s*(?<body>.*?)(?=^\[[^\]]+\]|\z)",
            RegexOptions.CultureInvariant);
        Assert.True(match.Success, $"Section [{sectionName}] was not found.");
        return match.Groups["body"].Value;
    }

    private static void AssertUtf8Bom(string path)
    {
        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes.Length >= 3, $"PowerShell script is unexpectedly empty: {path}");
        Assert.Equal(0xEF, bytes[0]);
        Assert.Equal(0xBB, bytes[1]);
        Assert.Equal(0xBF, bytes[2]);
    }
}
