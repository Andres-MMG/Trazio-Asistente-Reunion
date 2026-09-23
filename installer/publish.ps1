param([string]$Configuration = "Release")

$ErrorActionPreference = "Stop"
$expectedVersion = "0.2.0-beta.1"
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

$requiredFiles = @("Trazio.AsistenteReunion.exe", "Trazio.AsistenteReunion.Worker.exe", "Whisper.net.dll", "README.md", "trazio-capabilities.json")
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
$publishedCapabilities = @($capabilityManifest.capabilities | ForEach-Object { "$($_.id)-v$($_.version)" })
$requiredCapabilities = @("history-review-workspace-v1", "encrypted-audio-retention-v1", "obsidian-markdown-export-v1")
$missingCapabilities = @($requiredCapabilities | Where-Object { $_ -notin $publishedCapabilities })
if ($missingCapabilities.Count -gt 0) {
    throw "Published application is missing required capabilities: $($missingCapabilities -join ', '). Refusing to publish a stale package."
}
$appVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $publishOutput "Trazio.AsistenteReunion.exe")).ProductVersion
$workerVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $publishOutput "Trazio.AsistenteReunion.Worker.exe")).ProductVersion
if (-not $appVersion.StartsWith($expectedVersion) -or -not $workerVersion.StartsWith($expectedVersion)) {
    throw "Published version mismatch. App=$appVersion Worker=$workerVersion"
}
& (Join-Path $PSScriptRoot "smoke-worker.ps1") -WorkerPath (Join-Path $publishOutput "Trazio.AsistenteReunion.Worker.exe")

Write-Host "Published Trazio Asistente Reunión $expectedVersion to $publishOutput"

