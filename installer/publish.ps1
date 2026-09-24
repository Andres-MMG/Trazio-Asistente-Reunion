param([string]$Configuration = "Release")

$ErrorActionPreference = "Stop"
$expectedVersion = "0.2.0-beta.5"
$expectedArchiveName = "Trazio-Asistente-Reunion-v$expectedVersion-win-x64.zip"
$expectedChecksumName = "$expectedArchiveName.sha256"
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root "artifacts"
$publishOutput = Join-Path $artifacts "publish"

foreach ($target in @($publishOutput)) {
    $resolvedTarget = [System.IO.Path]::GetFullPath($target)
    $resolvedArtifacts = [System.IO.Path]::GetFullPath($artifacts) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $resolvedTarget.StartsWith($resolvedArtifacts, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove a path outside the artifacts directory: $resolvedTarget"
    }
    Remove-Item -LiteralPath $resolvedTarget -Recurse -Force -ErrorAction SilentlyContinue
}
New-Item -ItemType Directory -Force $publishOutput | Out-Null
dotnet publish (Join-Path $root "src\Trazio.AsistenteReunion.App\Trazio.AsistenteReunion.App.csproj") -c $Configuration -r win-x64 --self-contained true -p:PublishSingleFile=false -o $publishOutput
if ($LASTEXITCODE -ne 0) { throw "Application publish failed with exit code $LASTEXITCODE" }
dotnet publish (Join-Path $root "src\Trazio.AsistenteReunion.Worker\Trazio.AsistenteReunion.Worker.csproj") -c $Configuration -r win-x64 --self-contained true -p:PublishSingleFile=false -o $publishOutput
if ($LASTEXITCODE -ne 0) { throw "Worker publish failed with exit code $LASTEXITCODE" }
Copy-Item (Join-Path $root "README.md"), (Join-Path $root "THIRD-PARTY-NOTICES.md") $publishOutput -Force

$publishedSymbols = @(Get-ChildItem -LiteralPath $publishOutput -Filter "*.pdb" -File -Recurse)
foreach ($publishedSymbol in $publishedSymbols) {
    Remove-Item -LiteralPath $publishedSymbol.FullName -Force -ErrorAction Stop
}
$remainingSymbols = @(Get-ChildItem -LiteralPath $publishOutput -Filter "*.pdb" -File -Recurse)
if ($remainingSymbols.Count -gt 0) {
    throw "Published layout still contains debugging symbols: $($remainingSymbols.FullName -join ', ')"
}

$prohibitedExtensions = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
@(
    ".db", ".db-wal", ".db-shm", ".sqlite", ".sqlite3",
    ".wav", ".mp3", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".pcm", ".raw",
    ".ggml", ".gguf",
    ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".svg", ".ico", ".avif", ".heic",
    ".mp4", ".m4v", ".mkv", ".avi", ".mov", ".webm", ".wmv", ".mpeg", ".mpg",
    ".dmp", ".mdmp", ".dump", ".crash", ".core", ".log", ".etl", ".evtx", ".trace", ".har",
    ".key", ".pem", ".pfx", ".p12", ".env"
) | ForEach-Object { $null = $prohibitedExtensions.Add($_) }
$prohibitedDirectoryNames = [System.Collections.Generic.HashSet[string]]::new(
    [string[]]@("audio", "models", "logs", "dumps", "tools", "evaluation"),
    [System.StringComparer]::OrdinalIgnoreCase)
$prohibitedExactNames = [System.Collections.Generic.HashSet[string]]::new(
    [string[]]@("master.key", "settings.dat", ".env"),
    [System.StringComparer]::OrdinalIgnoreCase)
$prohibitedEvaluatorFiles = [System.Collections.Generic.HashSet[string]]::new(
    [string[]]@(
        "Trazio.AsistenteReunion.VisualEvaluation.exe",
        "Trazio.AsistenteReunion.VisualEvaluation.dll",
        "Trazio.AsistenteReunion.VisualEvaluation.deps.json",
        "Trazio.AsistenteReunion.VisualEvaluation.runtimeconfig.json"),
    [System.StringComparer]::OrdinalIgnoreCase)
$prohibitedEvaluationDataFiles = [System.Collections.Generic.HashSet[string]]::new(
    [string[]]@(
        "synthetic-corpus-v1.json",
        "synthetic-corpus-v1.golden.json"),
    [System.StringComparer]::OrdinalIgnoreCase)
$allowedPublishExtensions = [System.Collections.Generic.HashSet[string]]::new(
    [string[]]@(".dll", ".exe", ".json", ".md"),
    [System.StringComparer]::OrdinalIgnoreCase)
$allowedMetadataFiles = [System.Collections.Generic.HashSet[string]]::new(
    [string[]]@(
        "Trazio.AsistenteReunion.deps.json",
        "Trazio.AsistenteReunion.runtimeconfig.json",
        "Trazio.AsistenteReunion.Worker.deps.json",
        "Trazio.AsistenteReunion.Worker.runtimeconfig.json",
        "trazio-capabilities.json",
        "README.md",
        "THIRD-PARTY-NOTICES.md"),
    [System.StringComparer]::OrdinalIgnoreCase)
$allowedExecutableFiles = [System.Collections.Generic.HashSet[string]]::new(
    [string[]]@(
        "Trazio.AsistenteReunion.exe",
        "Trazio.AsistenteReunion.Worker.exe",
        "createdump.exe"),
    [System.StringComparer]::OrdinalIgnoreCase)
$prohibitedEntries = @(
    Get-ChildItem -LiteralPath $publishOutput -Recurse -Force | Where-Object {
        if ($_.PSIsContainer) {
            return $prohibitedDirectoryNames.Contains($_.Name)
        }

        $extension = [System.IO.Path]::GetExtension($_.Name)
        return -not $allowedPublishExtensions.Contains($extension) -or
            (($extension.Equals(".json", [System.StringComparison]::OrdinalIgnoreCase) -or
                $extension.Equals(".md", [System.StringComparison]::OrdinalIgnoreCase)) -and
                -not $allowedMetadataFiles.Contains($_.Name)) -or
            ($extension.Equals(".exe", [System.StringComparison]::OrdinalIgnoreCase) -and
                -not $allowedExecutableFiles.Contains($_.Name)) -or
            $prohibitedExtensions.Contains($extension) -or
            $prohibitedExactNames.Contains($_.Name) -or
            $prohibitedEvaluatorFiles.Contains($_.Name) -or
            $prohibitedEvaluationDataFiles.Contains($_.Name) -or
            $_.Name.StartsWith(".env.", [System.StringComparison]::OrdinalIgnoreCase) -or
            $_.Name.StartsWith("trazio-transcripts.db", [System.StringComparison]::OrdinalIgnoreCase) -or
            ($_.Name.StartsWith("ggml-", [System.StringComparison]::OrdinalIgnoreCase) -and
                $_.Name.EndsWith(".bin", [System.StringComparison]::OrdinalIgnoreCase))
    }
)
if ($prohibitedEntries.Count -gt 0) {
    $relativeEntries = @($prohibitedEntries | ForEach-Object {
        $_.FullName.Substring($publishOutput.Length).TrimStart([char[]]"\/")
    })
    throw "Published layout contains private or unsupported retained artifacts: $($relativeEntries -join ', ')"
}

$requiredFiles = @(
    "Trazio.AsistenteReunion.exe",
    "Trazio.AsistenteReunion.Worker.exe",
    "Trazio.AsistenteReunion.VisualAnalysis.dll",
    "Whisper.net.dll",
    "README.md",
    "trazio-capabilities.json")
foreach ($requiredFile in $requiredFiles) {
    $requiredPath = Join-Path $publishOutput $requiredFile
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) { throw "Published layout is missing $requiredFile" }
}

$capabilityManifestPath = Join-Path $publishOutput "trazio-capabilities.json"
try {
    $capabilityManifest = Get-Content -LiteralPath $capabilityManifestPath -Raw | ConvertFrom-Json -ErrorAction Stop
}
catch {
    throw "Published capability manifest is invalid: $($_.Exception.Message)"
}
if ($capabilityManifest.schemaVersion -ne 1 -or $capabilityManifest.product -ne "Trazio Asistente Reunión") {
    throw "Published capability manifest has an unsupported schema or product."
}
$capabilities = @($capabilityManifest.capabilities)
if ($capabilities.Count -eq 0) {
    throw "Published capability manifest does not declare any capabilities."
}
$seenCapabilityIds = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
foreach ($capability in $capabilities) {
    $capabilityId = [string]$capability.id
    $parsedVersion = 0
    if ([string]::IsNullOrWhiteSpace($capabilityId) -or
        -not [int]::TryParse([string]$capability.version, [ref]$parsedVersion) -or
        $parsedVersion -lt 1) {
        throw "Published capability manifest contains an invalid capability id or version."
    }
    if (-not $seenCapabilityIds.Add($capabilityId)) {
        throw "Published capability manifest contains duplicate capability id: $capabilityId"
    }
}
$requiredCapabilities = @(
    [pscustomobject]@{ Id = "history-review-workspace"; Version = 1; Label = "history-review-workspace-v1" }
    [pscustomobject]@{ Id = "encrypted-audio-retention"; Version = 1; Label = "encrypted-audio-retention-v1" }
    [pscustomobject]@{ Id = "obsidian-markdown-export"; Version = 1; Label = "obsidian-markdown-export-v1" }
    [pscustomobject]@{ Id = "meeting-window-provider-association-v1"; Version = 1; Label = "meeting-window-provider-association-v1" }
    [pscustomobject]@{ Id = "consented-ephemeral-window-capture-v1"; Version = 1; Label = "consented-ephemeral-window-capture-v1" }
)
if ($capabilities.Count -ne $requiredCapabilities.Count) {
    throw "Published capability manifest must declare exactly $($requiredCapabilities.Count) capabilities. Found $($capabilities.Count)."
}
$missingCapabilities = @(
    foreach ($requiredCapability in $requiredCapabilities) {
        $match = @(
            $capabilities | Where-Object {
                $_.id -eq $requiredCapability.Id -and $_.version -eq $requiredCapability.Version
            }
        )
        if ($match.Count -ne 1) {
            $requiredCapability.Label
        }
    }
)
if ($missingCapabilities.Count -gt 0) {
    throw "Published application is missing required capabilities: $($missingCapabilities -join ', '). Refusing to publish a stale package."
}
$appVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $publishOutput "Trazio.AsistenteReunion.exe")).ProductVersion
$workerVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $publishOutput "Trazio.AsistenteReunion.Worker.exe")).ProductVersion
$visualAnalysisVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $publishOutput "Trazio.AsistenteReunion.VisualAnalysis.dll")).ProductVersion
if (-not $appVersion.StartsWith($expectedVersion) -or
    -not $workerVersion.StartsWith($expectedVersion) -or
    -not $visualAnalysisVersion.StartsWith($expectedVersion)) {
    throw "Published version mismatch. App=$appVersion Worker=$workerVersion VisualAnalysis=$visualAnalysisVersion"
}
& (Join-Path $PSScriptRoot "smoke-worker.ps1") -WorkerPath (Join-Path $publishOutput "Trazio.AsistenteReunion.Worker.exe")

Write-Host "Published Trazio Asistente Reunión $expectedVersion to $publishOutput"
Write-Host "Expected release assets: $expectedArchiveName and $expectedChecksumName"

