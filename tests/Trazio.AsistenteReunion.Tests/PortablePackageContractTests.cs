using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Trazio.AsistenteReunion.Tests;

public sealed class PortablePackageContractTests
{
    [Fact]
    public void Script_DeclaresCanonicalDeterministicAndFailClosedContract()
    {
        var scriptPath = Path.Combine(FindRepositoryRoot(), "installer", "package-portable.ps1");
        var script = File.ReadAllText(scriptPath);
        var bytes = File.ReadAllBytes(scriptPath);

        Assert.True(bytes.Length >= 3);
        Assert.Equal(0xEF, bytes[0]);
        Assert.Equal(0xBB, bytes[1]);
        Assert.Equal(0xBF, bytes[2]);
        Assert.Contains("System.IO.Compression.ZipArchive", script, StringComparison.Ordinal);
        Assert.Contains("CompressionLevel]::Optimal", script, StringComparison.Ordinal);
        Assert.Contains("DateTimeOffset]::new(2000, 1, 1", script, StringComparison.Ordinal);
        Assert.Contains("System.StringComparer]::Ordinal", script, StringComparison.Ordinal);
        Assert.Contains("FileAttributes]::ReparsePoint", script, StringComparison.Ordinal);
        Assert.Contains("Assert-PayloadMatchesManifest", script, StringComparison.Ordinal);
        Assert.Contains("Assert-ZipMatchesManifest", script, StringComparison.Ordinal);
        Assert.Contains("File]::Replace", script, StringComparison.Ordinal);
        Assert.Contains("AllowTestOverrides", script, StringComparison.Ordinal);
        Assert.Contains("TestWorkspaceRoot", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Compress-Archive", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Trazio-Asistente-Reunion-v0.2.0-beta.8-win-x64.zip", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Package_SamePayloadTwice_ProducesIdenticalVerifiedZip()
    {
        using var fixture = new PortablePackageFixture();
        var firstOutput = fixture.CreateOutputDirectory("first");
        var secondOutput = fixture.CreateOutputDirectory("second");

        var first = await fixture.PackageAsync(firstOutput, "synthetic-first.zip");
        var second = await fixture.PackageAsync(secondOutput, "synthetic-second.zip");

        Assert.Equal(0, first.ExitCode);
        Assert.Equal(0, second.ExitCode);
        var firstZip = Path.Combine(firstOutput, "synthetic-first.zip");
        var secondZip = Path.Combine(secondOutput, "synthetic-second.zip");
        Assert.Equal(File.ReadAllBytes(firstZip), File.ReadAllBytes(secondZip));
        Assert.Equal(Sha256(firstZip), Sha256(secondZip));
        AssertCanonicalSidecar(firstZip);
        AssertCanonicalSidecar(secondZip);
        AssertZipMatchesFixture(firstZip, fixture.Files);
        AssertZipMatchesFixture(secondZip, fixture.Files);
    }

    [Theory]
    [InlineData("tampered")]
    [InlineData("missing")]
    [InlineData("extra")]
    public async Task Package_PayloadDoesNotMatchManifest_IsRejectedWithoutArtifacts(string scenario)
    {
        using var fixture = new PortablePackageFixture();
        var output = fixture.CreateOutputDirectory(scenario);
        switch (scenario)
        {
            case "tampered":
                await File.WriteAllTextAsync(Path.Combine(fixture.PayloadDirectory, "a.txt"), "omega", new UTF8Encoding(false));
                break;
            case "missing":
                File.Delete(Path.Combine(fixture.PayloadDirectory, "nested", "b.bin"));
                break;
            case "extra":
                await File.WriteAllTextAsync(Path.Combine(fixture.PayloadDirectory, "extra.txt"), "extra", new UTF8Encoding(false));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario));
        }

        var result = await fixture.PackageAsync(output, "synthetic-invalid.zip");

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(File.Exists(Path.Combine(output, "synthetic-invalid.zip")));
        Assert.False(File.Exists(Path.Combine(output, "synthetic-invalid.zip.sha256")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(output));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("nested\\b.bin")]
    [InlineData("/absolute.txt")]
    public async Task Package_AmbiguousOrEscapingManifestPath_IsRejected(string invalidPath)
    {
        using var fixture = new PortablePackageFixture();
        fixture.WriteManifest([new ManifestFile(invalidPath, 0, new string('0', 64))]);
        var output = fixture.CreateOutputDirectory("invalid-path");

        var result = await fixture.PackageAsync(output, "synthetic-invalid-path.zip");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(Directory.EnumerateFileSystemEntries(output));
    }

    [Fact]
    public async Task Package_DuplicateManifestPath_IsRejected()
    {
        using var fixture = new PortablePackageFixture();
        var entry = fixture.Files[0];
        fixture.WriteManifest([entry, entry]);
        var output = fixture.CreateOutputDirectory("duplicate");

        var result = await fixture.PackageAsync(output, "synthetic-duplicate.zip");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(Directory.EnumerateFileSystemEntries(output));
    }

    [Fact]
    public async Task Package_ReparsePointInPayload_IsRejected()
    {
        using var fixture = new PortablePackageFixture();
        var target = Path.Combine(fixture.RootDirectory, "outside.txt");
        await File.WriteAllTextAsync(target, "outside", new UTF8Encoding(false));
        File.CreateSymbolicLink(Path.Combine(fixture.PayloadDirectory, "linked.txt"), target);
        var output = fixture.CreateOutputDirectory("reparse");

        var result = await fixture.PackageAsync(output, "synthetic-reparse.zip");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("reparse point", result.AllOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateFileSystemEntries(output));
    }

    [Fact]
    public async Task Package_ReparsePointManifest_IsRejected()
    {
        using var fixture = new PortablePackageFixture();
        var linkedManifest = Path.Combine(fixture.RootDirectory, "manifest-link.json");
        File.CreateSymbolicLink(linkedManifest, fixture.ManifestPath);
        var output = fixture.CreateOutputDirectory("manifest-reparse");

        var result = await fixture.PackageAsync(output, "synthetic-manifest-reparse.zip", manifestPath: linkedManifest);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("manifiesto", result.AllOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reparse point", result.AllOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateFileSystemEntries(output));
    }

    [Fact]
    public async Task Package_FailureAfterArchiveReplacement_RestoresPreviousPairAndCleansTransactionFiles()
    {
        using var fixture = new PortablePackageFixture();
        var output = fixture.CreateOutputDirectory("rollback");
        var archivePath = Path.Combine(output, "synthetic-rollback.zip");
        var checksumPath = $"{archivePath}.sha256";
        var previousArchive = Encoding.UTF8.GetBytes("previous-archive");
        var previousChecksum = Encoding.UTF8.GetBytes("previous-checksum\n");
        await File.WriteAllBytesAsync(archivePath, previousArchive);
        await File.WriteAllBytesAsync(checksumPath, previousChecksum);

        var result = await fixture.PackageAsync(output, "synthetic-rollback.zip", "AfterArchiveReplace");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(previousArchive, File.ReadAllBytes(archivePath));
        Assert.Equal(previousChecksum, File.ReadAllBytes(checksumPath));
        Assert.Equal(
            ["synthetic-rollback.zip", "synthetic-rollback.zip.sha256"],
            Directory.EnumerateFiles(output).Select(path => Path.GetFileName(path)!).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task Package_RollbackFailure_PreservesRecoverableBackupAndReportsItsPath()
    {
        using var fixture = new PortablePackageFixture();
        var output = fixture.CreateOutputDirectory("rollback-failure");
        var archivePath = Path.Combine(output, "synthetic-rollback-failure.zip");
        var checksumPath = $"{archivePath}.sha256";
        var previousArchive = Encoding.UTF8.GetBytes("previous-archive");
        await File.WriteAllBytesAsync(archivePath, previousArchive);
        await File.WriteAllTextAsync(checksumPath, "previous-checksum\n", new UTF8Encoding(false));

        var result = await fixture.PackageAsync(output, "synthetic-rollback-failure.zip", "DuringRollback");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Copias de respaldo preservadas", result.AllOutput, StringComparison.Ordinal);
        var backup = Assert.Single(Directory.EnumerateFiles(output, "*.backup"));
        Assert.Equal(previousArchive, File.ReadAllBytes(backup));
        Assert.DoesNotContain(Directory.EnumerateFiles(output), path => path.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Package_ProductionInvocationRejectsAnyExplicitOverride()
    {
        using var fixture = new PortablePackageFixture();

        var result = await fixture.InvokeScriptAsync(["-PayloadDirectory", fixture.PayloadDirectory]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("modo productivo no acepta", result.AllOutput, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertCanonicalSidecar(string zipPath)
    {
        var expected = $"{Sha256(zipPath)}  {Path.GetFileName(zipPath)}\n";
        var sidecarPath = $"{zipPath}.sha256";
        var bytes = File.ReadAllBytes(sidecarPath);

        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        Assert.DoesNotContain((byte)'\r', bytes);
        Assert.Equal(expected, File.ReadAllText(sidecarPath, new UTF8Encoding(false, true)));
    }

    private static void AssertZipMatchesFixture(string zipPath, IReadOnlyList<ManifestFile> expectedFiles)
    {
        using var stream = File.OpenRead(zipPath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        Assert.Equal(expectedFiles.Select(file => file.Path), archive.Entries.Select(entry => entry.FullName));
        foreach (var pair in archive.Entries.Zip(expectedFiles))
        {
            Assert.DoesNotContain('\\', pair.First.FullName);
            Assert.Equal(new DateTime(2000, 1, 1), pair.First.LastWriteTime.DateTime);
            Assert.Equal(0, pair.First.ExternalAttributes);
            Assert.Equal(pair.Second.Length, pair.First.Length);
            using var entryStream = pair.First.Open();
            Assert.Equal(pair.Second.Sha256, Convert.ToHexString(SHA256.HashData(entryStream)).ToLowerInvariant());
        }
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "installer", "package-portable.ps1")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found from the test output directory.");
    }

    private sealed class PortablePackageFixture : IDisposable
    {
        private readonly string _scriptPath = Path.Combine(FindRepositoryRoot(), "installer", "package-portable.ps1");

        public PortablePackageFixture()
        {
            RootDirectory = Path.Combine(Path.GetTempPath(), $"trazio-portable-package-{Guid.NewGuid():N}");
            PayloadDirectory = Path.Combine(RootDirectory, "payload");
            ManifestPath = Path.Combine(RootDirectory, "publish-manifest.json");
            Directory.CreateDirectory(Path.Combine(PayloadDirectory, "nested"));
            File.WriteAllText(Path.Combine(PayloadDirectory, "a.txt"), "alpha", new UTF8Encoding(false));
            File.WriteAllBytes(Path.Combine(PayloadDirectory, "nested", "b.bin"), Enumerable.Range(0, 256).Select(value => (byte)value).ToArray());
            Files =
            [
                ManifestEntry("a.txt"),
                ManifestEntry("nested/b.bin")
            ];
            WriteManifest(Files);
        }

        public string RootDirectory { get; }
        public string PayloadDirectory { get; }
        public string ManifestPath { get; }
        public IReadOnlyList<ManifestFile> Files { get; }

        public string CreateOutputDirectory(string name)
        {
            var output = Path.Combine(RootDirectory, $"output-{name}");
            Directory.CreateDirectory(output);
            return output;
        }

        public void WriteManifest(IReadOnlyList<ManifestFile> files)
        {
            var manifest = new
            {
                schemaVersion = 1,
                product = "Trazio Asistente Reunión",
                version = "0.0.0-test",
                releaseSequence = 1,
                files = files.Select(file => new { path = file.Path, length = file.Length, sha256 = file.Sha256 })
            };
            var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
            File.WriteAllText(ManifestPath, json, new UTF8Encoding(false));
        }

        public Task<ProcessResult> PackageAsync(
            string outputDirectory,
            string archiveName,
            string injectTestFailure = "None",
            string? manifestPath = null) =>
            InvokeScriptAsync(
            [
                "-AllowTestOverrides",
                "-PayloadDirectory", PayloadDirectory,
                "-PayloadManifestPath", manifestPath ?? ManifestPath,
                "-OutputDirectory", outputDirectory,
                "-ArchiveName", archiveName,
                "-TestWorkspaceRoot", RootDirectory,
                "-InjectTestFailure", injectTestFailure
            ]);

        public async Task<ProcessResult> InvokeScriptAsync(IReadOnlyList<string> arguments)
        {
            var startInfo = new ProcessStartInfo("pwsh")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-NoLogo");
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(_scriptPath);
            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);

            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start pwsh.");
            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return new ProcessResult(process.ExitCode, await standardOutput, await standardError);
        }

        public void Dispose()
        {
            if (Directory.Exists(RootDirectory))
                Directory.Delete(RootDirectory, true);
        }

        private ManifestFile ManifestEntry(string relativePath)
        {
            var fullPath = Path.Combine(PayloadDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
            var file = new FileInfo(fullPath);
            return new ManifestFile(relativePath, file.Length, Sha256(fullPath));
        }
    }

    private sealed record ManifestFile(string Path, long Length, string Sha256);

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
    {
        public string AllOutput => $"{StandardOutput}\n{StandardError}";
    }
}
