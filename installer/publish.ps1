param([string]$Configuration = "Release")

$ErrorActionPreference = "Stop"
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

$requiredFiles = @("Trazio.AsistenteReunion.exe", "Trazio.AsistenteReunion.Worker.exe", "Whisper.net.dll", "README.md")
foreach ($requiredFile in $requiredFiles) {
    $requiredPath = Join-Path $publishOutput $requiredFile
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) { throw "Published layout is missing $requiredFile" }
}

$appAssemblyPath = Join-Path $publishOutput "Trazio.AsistenteReunion.dll"
$publishedUi = [System.Text.Encoding]::UTF8.GetString([System.IO.File]::ReadAllBytes($appAssemblyPath))
$requiredHistoryMarkers = @("Historial", "Revisar segmento seleccionado", "Reemplazos detectados para el diccionario", "Almacenamiento local", "Comparar transcripciones", "El audio cifrado siempre se guarda para reproducirlo y retranscribirlo")
$missingHistoryMarkers = @($requiredHistoryMarkers | Where-Object { -not $publishedUi.Contains($_) })
if ($missingHistoryMarkers.Count -gt 0) {
    throw "Published application is missing the Stage 6A History workspace: $($missingHistoryMarkers -join ', '). Refusing to publish a stale UI."
}
$appVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $publishOutput "Trazio.AsistenteReunion.exe")).ProductVersion
$workerVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $publishOutput "Trazio.AsistenteReunion.Worker.exe")).ProductVersion
if (-not $appVersion.StartsWith("0.1.1") -or -not $workerVersion.StartsWith("0.1.1")) {
    throw "Published version mismatch. App=$appVersion Worker=$workerVersion"
}
& (Join-Path $PSScriptRoot "smoke-worker.ps1") -WorkerPath (Join-Path $publishOutput "Trazio.AsistenteReunion.Worker.exe")

Write-Host "Published Trazio Asistente Reunión 0.1.1 to $publishOutput"

