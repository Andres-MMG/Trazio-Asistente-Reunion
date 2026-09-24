param(
    [string]$IsccPath,
    [switch]$KeepWorkspace
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$buildScript = Join-Path $PSScriptRoot "build-installer.ps1"
$runId = [Guid]::NewGuid().ToString("N")
$workspace = Join-Path ([System.IO.Path]::GetTempPath()) "trazio-installer-harness-$runId"
$appRoot = Join-Path $workspace "program"
$outputRoot = Join-Path $workspace "installers"
$logRoot = Join-Path $workspace "logs"
$unrelatedSentinelRootA = Join-Path $workspace "unrelated-a"
$unrelatedSentinelRootB = Join-Path $workspace "unrelated-b"
$appGuid = [Guid]::NewGuid().ToString().ToUpperInvariant()
$registrySubkey = "Software\Trazio\InstallerHarness\$runId"
$startMenuGroup = "Trazio Installer Harness $runId"
$shortcutName = "Trazio Installer Harness $runId"
$appMutex = "Trazio.AsistenteReunion.Harness.$runId.App"
$setupMutex = "Trazio.AsistenteReunion.Harness.$runId.Setup"
$startMenuShortcutPath = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\$startMenuGroup\$shortcutName.lnk"
$desktopRoot = [Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)
$desktopShortcutPath = Join-Path $desktopRoot "$shortcutName.lnk"
$productDesktopShortcutPath = Join-Path $desktopRoot "Trazio Asistente Reunión.lnk"
$installerRegistryPath = "HKCU:\$registrySubkey"
$uninstallRegistryPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{$appGuid}_is1"
$mutexProcess = $null
$scenarioResults = [System.Collections.Generic.List[object]]::new()

function Read-Utf8Text {
    param([string]$Path)
    return [System.IO.File]::ReadAllText(
        [System.IO.Path]::GetFullPath($Path),
        [System.Text.UTF8Encoding]::new($false, $true))
}

function Get-NormalizedRelativePath {
    param([string]$BasePath, [string]$FullPath)

    $baseFullPath = [System.IO.Path]::GetFullPath($BasePath).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    $itemFullPath = [System.IO.Path]::GetFullPath($FullPath)
    if (-not $itemFullPath.StartsWith($baseFullPath, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "La ruta no pertenece al payload esperado: $itemFullPath"
    }
    return $itemFullPath.Substring($baseFullPath.Length).Replace('\', '/')
}

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) {
        throw $Message
    }
}

function Add-Pass {
    param([string]$Scenario, [string]$Evidence)
    $scenarioResults.Add([ordered]@{ scenario = $Scenario; status = "PASS"; evidence = $Evidence })
    Write-Host "PASS $Scenario - $Evidence"
}

function Write-PayloadManifest {
    param([string]$PayloadDirectory, [string]$ManifestPath, [string]$Version, [int]$Sequence)

    $relativePaths = [string[]]@(
        Get-ChildItem -LiteralPath $PayloadDirectory -File -Recurse | ForEach-Object {
            Get-NormalizedRelativePath -BasePath $PayloadDirectory -FullPath $_.FullName
        }
    )
    [Array]::Sort($relativePaths, [System.StringComparer]::Ordinal)
    $files = @(
        foreach ($relativePath in $relativePaths) {
            $fullPath = Join-Path $PayloadDirectory ($relativePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
            $file = Get-Item -LiteralPath $fullPath
            [ordered]@{
                path = $relativePath
                length = [long]$file.Length
                sha256 = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        }
    )
    $manifest = [ordered]@{
        schemaVersion = 1
        product = "Trazio Asistente Reunión"
        version = $Version
        releaseSequence = $Sequence
        files = $files
    }
    $json = ($manifest | ConvertTo-Json -Depth 5).Replace(
        [Environment]::NewLine,
        [string][char]10) + [char]10
    [System.IO.File]::WriteAllText($ManifestPath, $json, [System.Text.UTF8Encoding]::new($false))
}

function New-SyntheticPayload {
    param([string]$Label, [string]$Version, [int]$Sequence, [switch]$IncludeRollbackTrigger)

    $payloadDirectory = Join-Path $workspace "payload-$Label"
    $manifestPath = Join-Path $workspace "payload-$Label.manifest.json"
    New-Item -ItemType Directory -Force (Join-Path $payloadDirectory "runtime") | Out-Null
    [System.IO.File]::WriteAllText(
        (Join-Path $payloadDirectory "Trazio.AsistenteReunion.exe"),
        "synthetic-payload-$Label",
        [System.Text.UTF8Encoding]::new($false))
    [System.IO.File]::WriteAllText(
        (Join-Path $payloadDirectory "runtime\payload.txt"),
        "runtime-$Label",
        [System.Text.UTF8Encoding]::new($false))
    if ($IncludeRollbackTrigger) {
        [System.IO.File]::WriteAllText(
            (Join-Path $payloadDirectory "zz-rollback-trigger.bin"),
            "replacement-from-$Label",
            [System.Text.UTF8Encoding]::new($false))
    }
    Write-PayloadManifest -PayloadDirectory $payloadDirectory -ManifestPath $manifestPath -Version $Version -Sequence $Sequence
    return [pscustomobject]@{
        Label = $Label
        Version = $Version
        Sequence = $Sequence
        Directory = $payloadDirectory
        Manifest = $manifestPath
        ExpectedExecutableHash = (Get-FileHash -LiteralPath (Join-Path $payloadDirectory "Trazio.AsistenteReunion.exe") -Algorithm SHA256).Hash
    }
}

function Build-HarnessInstaller {
    param([object]$Payload)

    $arguments = @{
        SkipPublish = $true
        Version = $Payload.Version
        ReleaseSequence = $Payload.Sequence
        PayloadDirectory = $Payload.Directory
        PayloadManifestPath = $Payload.Manifest
        OutputDirectory = $outputRoot
        OutputBaseFilename = "Trazio-Harness-$($Payload.Label)"
        TestWorkspaceRoot = $workspace
        IsccPath = $IsccPath
        AllowTestOverrides = $true
        AppGuid = $appGuid
        DefaultDirName = $appRoot
        StartMenuGroup = $startMenuGroup
        ShortcutName = $shortcutName
        InstallerRegistrySubkey = $registrySubkey
        AppMutex = $appMutex
        SetupMutex = $setupMutex
    }
    & $buildScript @arguments | Out-Host
    $installerPath = Join-Path $outputRoot "Trazio-Harness-$($Payload.Label).exe"
    Assert-True (Test-Path -LiteralPath $installerPath -PathType Leaf) "No se generó $installerPath"
    return $installerPath
}

function Assert-InstallerSidecars {
    param([string]$InstallerPath, [object]$Payload)

    $checksumPath = "$InstallerPath.sha256"
    $manifestPath = "$InstallerPath.manifest.json"
    Assert-True (Test-Path -LiteralPath $checksumPath -PathType Leaf) "Falta el sidecar SHA-256."
    Assert-True (Test-Path -LiteralPath $manifestPath -PathType Leaf) "Falta el manifest del instalador."
    $manifestBytes = [System.IO.File]::ReadAllBytes($manifestPath)
    Assert-True ($manifestBytes.Length -gt 0) "El manifest del instalador está vacío."
    Assert-True (-not ($manifestBytes.Length -ge 3 -and $manifestBytes[0] -eq 0xEF -and $manifestBytes[1] -eq 0xBB -and $manifestBytes[2] -eq 0xBF)) "El manifest del instalador no debe llevar BOM."
    Assert-True (-not ($manifestBytes -contains 13)) "El manifest del instalador debe usar solo LF."
    Assert-True ($manifestBytes[$manifestBytes.Length - 1] -eq 10) "El manifest del instalador debe terminar en LF."
    $manifest = Read-Utf8Text $manifestPath | ConvertFrom-Json -ErrorAction Stop
    $expectedFields = @("schemaVersion", "product", "version", "releaseSequence", "file", "length", "sha256", "payloadManifest", "payloadManifestSha256", "authenticode")
    $actualFields = @($manifest.PSObject.Properties.Name)
    Assert-True (($actualFields -join '|') -ceq ($expectedFields -join '|')) "El manifest del instalador no contiene exactamente los campos esperados y en orden."

    $installerFile = Get-Item -LiteralPath $InstallerPath
    $actualHash = (Get-FileHash -LiteralPath $InstallerPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $payloadManifestHash = (Get-FileHash -LiteralPath $Payload.Manifest -Algorithm SHA256).Hash.ToLowerInvariant()
    Assert-True ([int]$manifest.schemaVersion -eq 1) "El schemaVersion del instalador no coincide."
    Assert-True ([string]$manifest.product -ceq "Trazio Asistente Reunión") "El producto del manifest del instalador no coincide."
    Assert-True ([string]$manifest.version -ceq $Payload.Version) "La versión del manifest del instalador no coincide."
    Assert-True ([int]$manifest.releaseSequence -eq $Payload.Sequence) "La secuencia del manifest del instalador no coincide."
    Assert-True ([string]$manifest.file -ceq $installerFile.Name) "El nombre del instalador no coincide con su manifest."
    Assert-True ([long]$manifest.length -eq [long]$installerFile.Length) "La longitud del instalador no coincide con su manifest."
    Assert-True ([string]$manifest.sha256 -cmatch '^[0-9a-f]{64}$') "El SHA-256 del instalador no tiene formato canónico."
    Assert-True ([string]$manifest.sha256 -ceq $actualHash) "El manifest del instalador no coincide con el binario."
    Assert-True ([string]$manifest.payloadManifest -ceq [System.IO.Path]::GetFileName($Payload.Manifest)) "El nombre del manifest de payload no coincide."
    Assert-True ([string]$manifest.payloadManifestSha256 -cmatch '^[0-9a-f]{64}$') "El SHA-256 del manifest de payload no tiene formato canónico."
    Assert-True ([string]$manifest.payloadManifestSha256 -ceq $payloadManifestHash) "El SHA-256 del manifest de payload no coincide."
    Assert-True ([string]$manifest.authenticode -ceq "unsigned") "El harness esperaba un instalador sin firma."
    Assert-True ((Get-AuthenticodeSignature -LiteralPath $InstallerPath).Status -eq [System.Management.Automation.SignatureStatus]::NotSigned) "El estado Authenticode real no coincide con el manifest."

    $checksumText = Read-Utf8Text $checksumPath
    $expectedChecksumText = "$actualHash  $($installerFile.Name)" + [char]10
    Assert-True ($checksumText -ceq $expectedChecksumText) "El sidecar SHA-256 no tiene nombre, digest o formato canónicos."
}

function Invoke-Setup {
    param([string]$InstallerPath, [string]$Scenario, [bool]$ExpectSuccess)

    $logPath = Join-Path $logRoot "$Scenario.log"
    $process = Start-Process -FilePath $InstallerPath -ArgumentList @(
        "/VERYSILENT",
        "/SUPPRESSMSGBOXES",
        "/NORESTART",
        "/SP-",
        "/MERGETASKS=desktopicon",
        "/LOG=$logPath") -Wait -PassThru
    $exitCode = $process.ExitCode
    if ($ExpectSuccess -and $exitCode -ne 0) {
        throw "$Scenario falló con código $exitCode. Log: $logPath"
    }
    if (-not $ExpectSuccess -and $exitCode -eq 0) {
        throw "$Scenario debía fallar de forma segura, pero terminó con código 0."
    }
    return $exitCode
}

function Get-ShortcutTarget {
    param([string]$Path)

    Assert-True (Test-Path -LiteralPath $Path -PathType Leaf) "No existe el acceso directo desechable: $Path"
    $shell = New-Object -ComObject WScript.Shell
    try {
        return $shell.CreateShortcut($Path).TargetPath
    }
    finally {
        [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null
    }
}

function Get-FileSnapshot {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return [pscustomobject]@{ Exists = $false; Length = 0L; Sha256 = $null }
    }
    $file = Get-Item -LiteralPath $Path
    return [pscustomobject]@{
        Exists = $true
        Length = [long]$file.Length
        Sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    }
}

function Assert-FileSnapshot {
    param([string]$Path, [object]$Expected, [string]$Description)

    $actual = Get-FileSnapshot -Path $Path
    Assert-True ($actual.Exists -eq $Expected.Exists) "$Description cambió su estado de existencia."
    if ($Expected.Exists) {
        Assert-True ($actual.Length -eq $Expected.Length -and $actual.Sha256 -eq $Expected.Sha256) "$Description fue reemplazado o modificado."
    }
}

function Assert-ProductDesktopShortcutPreserved {
    Assert-FileSnapshot -Path $productDesktopShortcutPath -Expected $productDesktopShortcutSnapshot -Description "El acceso directo productivo del escritorio"
}

function Assert-ActiveVersion {
    param([object]$Payload)

    $installedExecutable = Join-Path $appRoot "versions\$($Payload.Version)\Trazio.AsistenteReunion.exe"
    Assert-True (Test-Path -LiteralPath $installedExecutable -PathType Leaf) "Falta el payload $($Payload.Version)."
    Assert-True ((Get-FileHash -LiteralPath $installedExecutable -Algorithm SHA256).Hash -eq $Payload.ExpectedExecutableHash) "El payload activo no coincide."
    $registry = Get-ItemProperty -LiteralPath $installerRegistryPath
    Assert-True ([int]$registry.ReleaseSequence -eq $Payload.Sequence) "La secuencia activa no coincide."
    Assert-True ([string]$registry.Version -ceq $Payload.Version) "La versión activa no coincide."
    $expectedTarget = [System.IO.Path]::GetFullPath($installedExecutable)
    Assert-True ([System.IO.Path]::GetFullPath((Get-ShortcutTarget -Path $startMenuShortcutPath)) -ceq $expectedTarget) "El acceso directo desechable del menú Inicio no apunta al payload activo."
    Assert-True ([System.IO.Path]::GetFullPath((Get-ShortcutTarget -Path $desktopShortcutPath)) -ceq $expectedTarget) "El acceso directo desechable del escritorio no apunta al payload activo."
    Assert-ProductDesktopShortcutPreserved
}

function Get-UnrelatedSentinelHashes {
    $result = [ordered]@{}
    foreach ($sentinel in Get-ChildItem -LiteralPath $unrelatedSentinelRootA, $unrelatedSentinelRootB -File -Recurse) {
        $result[$sentinel.FullName] = (Get-FileHash -LiteralPath $sentinel.FullName -Algorithm SHA256).Hash
    }
    return $result
}

function Assert-UnrelatedSentinels {
    param([System.Collections.IDictionary]$Expected)
    foreach ($entry in $Expected.GetEnumerator()) {
        Assert-True (Test-Path -LiteralPath $entry.Key -PathType Leaf) "El instalador eliminó un archivo ajeno a su árbol de programa."
        Assert-True ((Get-FileHash -LiteralPath $entry.Key -Algorithm SHA256).Hash -eq $entry.Value) "El instalador modificó un archivo ajeno a su árbol de programa."
    }
}

function Set-ModernInstallerState {
    param([string]$Version, [int]$Sequence)

    New-Item -ItemType Directory -Force $installerRegistryPath | Out-Null
    New-ItemProperty -LiteralPath $installerRegistryPath -Name "Version" -PropertyType String -Value $Version -Force | Out-Null
    New-ItemProperty -LiteralPath $installerRegistryPath -Name "ReleaseSequence" -PropertyType DWord -Value $Sequence -Force | Out-Null
}

function Start-MutexHolder {
    $readyPath = Join-Path $workspace "mutex-ready"
    $script = @'
$mutex = [System.Threading.Mutex]::new($false, '__MUTEX__')
[System.IO.File]::WriteAllText('__READY__', 'ready')
try { Start-Sleep -Seconds 120 } finally { $mutex.Dispose() }
'@
    $script = $script.Replace("__MUTEX__", $appMutex.Replace("'", "''"))
    $script = $script.Replace("__READY__", $readyPath.Replace("'", "''"))
    $encoded = [Convert]::ToBase64String([System.Text.Encoding]::Unicode.GetBytes($script))
    $process = Start-Process -FilePath (Get-Process -Id $PID).Path -ArgumentList "-NoProfile", "-EncodedCommand", $encoded -WindowStyle Hidden -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while (-not (Test-Path -LiteralPath $readyPath -PathType Leaf)) {
        if ($process.HasExited -or [DateTime]::UtcNow -ge $deadline) {
            throw "No se pudo iniciar el retenedor de mutex desechable."
        }
        Start-Sleep -Milliseconds 100
    }
    return $process
}

function Remove-HarnessState {
    if ($script:mutexProcess -and -not $script:mutexProcess.HasExited) {
        Stop-Process -Id $script:mutexProcess.Id -Force -ErrorAction SilentlyContinue
        $script:mutexProcess.WaitForExit(5000) | Out-Null
    }
    $uninstaller = Join-Path $appRoot "unins000.exe"
    if (Test-Path -LiteralPath $uninstaller -PathType Leaf) {
        Start-Process -FilePath $uninstaller -ArgumentList @(
            "/VERYSILENT",
            "/SUPPRESSMSGBOXES",
            "/NORESTART") -Wait | Out-Null
    }
    Remove-Item -LiteralPath $installerRegistryPath, $uninstallRegistryPath -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath (Split-Path -Parent $startMenuShortcutPath) -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $desktopShortcutPath -Force -ErrorAction SilentlyContinue

    if (-not $KeepWorkspace -and (Test-Path -LiteralPath $workspace)) {
        $resolvedWorkspace = [System.IO.Path]::GetFullPath($workspace)
        $resolvedTemp = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
        Assert-True ($resolvedWorkspace.StartsWith($resolvedTemp, [System.StringComparison]::OrdinalIgnoreCase)) "Se rechazó limpiar una ruta fuera de TEMP."
        $removed = $false
        for ($attempt = 1; $attempt -le 20 -and -not $removed; $attempt++) {
            try {
                Remove-Item -LiteralPath $resolvedWorkspace -Recurse -Force
                $removed = $true
            }
            catch {
                if ($attempt -eq 20) { throw }
                Start-Sleep -Milliseconds 250
            }
        }
    }
}

New-Item -ItemType Directory -Force $workspace, $outputRoot, $logRoot, $unrelatedSentinelRootA, $unrelatedSentinelRootB | Out-Null
[System.IO.File]::WriteAllText((Join-Path $unrelatedSentinelRootA "unrelated-a.bin"), "unrelated-a", [System.Text.UTF8Encoding]::new($false))
[System.IO.File]::WriteAllText((Join-Path $unrelatedSentinelRootB "unrelated-b.bin"), "unrelated-b", [System.Text.UTF8Encoding]::new($false))
$unrelatedSentinelHashes = Get-UnrelatedSentinelHashes
$productDesktopShortcutSnapshot = Get-FileSnapshot -Path $productDesktopShortcutPath

Assert-True ($appGuid -ne "8C7AF6E0-31F1-4E2E-BF21-8A9AC6D1DDF1") "El harness no puede usar el AppId productivo."
Assert-True ([System.IO.Path]::GetFullPath($appRoot).StartsWith([System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()), [System.StringComparison]::OrdinalIgnoreCase)) "El harness debe instalar exclusivamente bajo TEMP."
Assert-True ([System.IO.Path]::GetFullPath($appRoot).StartsWith([System.IO.Path]::GetFullPath($workspace), [System.StringComparison]::OrdinalIgnoreCase)) "La raíz de programa del harness debe pertenecer al workspace desechable."
Assert-True ([System.IO.Path]::GetFullPath($outputRoot).StartsWith([System.IO.Path]::GetFullPath($workspace), [System.StringComparison]::OrdinalIgnoreCase)) "La salida del harness debe pertenecer al workspace desechable."
Assert-True ($startMenuGroup -ne "Trazio Asistente Reunión") "El harness no puede usar el grupo productivo."
Assert-True ($shortcutName -ne "Trazio Asistente Reunión" -and $shortcutName.Contains($runId)) "El harness debe usar un nombre de acceso directo único ligado al runId."
Assert-True ($registrySubkey -ne "Software\Trazio\AsistenteReunion\Installer") "El harness no puede usar el registro productivo."
Assert-True ($appMutex -ne "Trazio.AsistenteReunion.AppRunning.v1") "El harness no puede usar el mutex productivo."
Assert-True ($setupMutex -ne "Trazio.AsistenteReunion.Setup.v1") "El harness no puede usar el mutex de Setup productivo."

try {
    $payloadA = New-SyntheticPayload -Label "A" -Version "0.2.0-beta.4" -Sequence 5
    $payloadB = New-SyntheticPayload -Label "B" -Version "0.2.0-beta.5" -Sequence 6
    $payloadC = New-SyntheticPayload -Label "C-failure" -Version "0.2.0-beta.6-harness" -Sequence 7 -IncludeRollbackTrigger

    $installerA = Build-HarnessInstaller -Payload $payloadA
    $installerB = Build-HarnessInstaller -Payload $payloadB
    $installerC = Build-HarnessInstaller -Payload $payloadC
    Assert-InstallerSidecars $installerA $payloadA
    Assert-InstallerSidecars $installerB $payloadB
    Assert-InstallerSidecars $installerC $payloadC
    Add-Pass "manifest-checksum" "todos los campos, nombres, longitudes, hashes y formatos de los tres sidecars coinciden; Authenticode está unsigned"

    $productOverrideCases = [ordered]@{
        Configuration = "Debug"
        SkipPublish = $true
        Version = "0.2.0-beta.5"
        ReleaseSequence = 6
        PayloadDirectory = (Join-Path $root "artifacts\publish")
        PayloadManifestPath = (Join-Path $root "artifacts\publish-manifest.json")
        OutputDirectory = (Join-Path $root "artifacts\installer")
        OutputBaseFilename = "Trazio-Asistente-Reunion-v0.2.0-beta.5-Setup"
        TestWorkspaceRoot = $workspace
        AppGuid = "8C7AF6E0-31F1-4E2E-BF21-8A9AC6D1DDF1"
        DefaultDirName = "{localappdata}\Programs\Trazio Asistente Reunion"
        StartMenuGroup = "Trazio Asistente Reunión"
        ShortcutName = "Trazio Asistente Reunión"
        InstallerRegistrySubkey = "Software\Trazio\AsistenteReunion\Installer"
        AppMutex = "Trazio.AsistenteReunion.AppRunning.v1"
        SetupMutex = "Trazio.AsistenteReunion.Setup.v1"
    }
    foreach ($overrideCase in $productOverrideCases.GetEnumerator()) {
        $singleOverride = @{ $overrideCase.Key = $overrideCase.Value }
        $overrideWithoutOptInRejected = $false
        try {
            & $buildScript @singleOverride | Out-Host
        }
        catch {
            $overrideWithoutOptInRejected = $_.Exception.Message -like "*AllowTestOverrides*"
        }
        Assert-True $overrideWithoutOptInRejected "El build productivo aceptó el override $($overrideCase.Key) sin habilitar el modo desechable."
    }

    $partialDisposableRejected = $false
    try {
        & $buildScript -AllowTestOverrides -SkipPublish -Version $payloadA.Version -ReleaseSequence $payloadA.Sequence `
            -PayloadDirectory $payloadA.Directory -PayloadManifestPath $payloadA.Manifest -TestWorkspaceRoot $workspace | Out-Host
    }
    catch {
        $partialDisposableRejected = $_.Exception.Message -like "*todos los parámetros explícitos*"
    }
    Assert-True $partialDisposableRejected "El modo desechable aceptó una identidad/salida incompleta."

    $safeTestArguments = @{
        SkipPublish = $true
        Version = $payloadA.Version
        ReleaseSequence = $payloadA.Sequence
        PayloadDirectory = $payloadA.Directory
        PayloadManifestPath = $payloadA.Manifest
        OutputDirectory = $outputRoot
        OutputBaseFilename = "Trazio-Harness-IdentityGuard"
        TestWorkspaceRoot = $workspace
        IsccPath = $IsccPath
        AllowTestOverrides = $true
        AppGuid = $appGuid
        DefaultDirName = $appRoot
        StartMenuGroup = $startMenuGroup
        ShortcutName = $shortcutName
        InstallerRegistrySubkey = $registrySubkey
        AppMutex = $appMutex
        SetupMutex = $setupMutex
    }
    $productIdentityCases = [ordered]@{
        AppGuid = "8C7AF6E0-31F1-4E2E-BF21-8A9AC6D1DDF1"
        DefaultDirName = "{localappdata}\Programs\Trazio Asistente Reunion"
        StartMenuGroup = "Trazio Asistente Reunión"
        ShortcutName = "Trazio Asistente Reunión"
        InstallerRegistrySubkey = "Software\Trazio\AsistenteReunion\Installer"
        AppMutex = "Trazio.AsistenteReunion.AppRunning.v1"
        SetupMutex = "Trazio.AsistenteReunion.Setup.v1"
    }
    foreach ($identityCase in $productIdentityCases.GetEnumerator()) {
        $mixedArguments = @{}
        foreach ($entry in $safeTestArguments.GetEnumerator()) { $mixedArguments[$entry.Key] = $entry.Value }
        $mixedArguments[$identityCase.Key] = $identityCase.Value
        $mixedIdentityRejected = $false
        try {
            & $buildScript @mixedArguments | Out-Host
        }
        catch {
            $mixedIdentityRejected = $_.Exception.Message -like "*prohíbe mezclar identidades productivas*"
        }
        Assert-True $mixedIdentityRejected "El build aceptó una mezcla con la identidad productiva $($identityCase.Key)."
    }
    Add-Pass "build-mode-isolation" "cada override exige opt-in, el modo desechable exige todos los parámetros y cada identidad productiva se rechaza individualmente"

    $tamperedPayload = Join-Path $workspace "payload-tampered"
    Copy-Item -LiteralPath $payloadB.Directory -Destination $tamperedPayload -Recurse
    [System.IO.File]::AppendAllText((Join-Path $tamperedPayload "runtime\payload.txt"), "-tampered")
    $tamperRejected = $false
    try {
        & $buildScript -SkipPublish -Version $payloadB.Version -ReleaseSequence $payloadB.Sequence `
            -PayloadDirectory $tamperedPayload -PayloadManifestPath $payloadB.Manifest `
            -OutputDirectory $outputRoot -OutputBaseFilename "Trazio-Harness-Tampered" -IsccPath $IsccPath `
            -TestWorkspaceRoot $workspace -AllowTestOverrides -AppGuid $appGuid -DefaultDirName $appRoot -StartMenuGroup $startMenuGroup `
            -ShortcutName $shortcutName -InstallerRegistrySubkey $registrySubkey -AppMutex $appMutex -SetupMutex $setupMutex
    }
    catch {
        $tamperRejected = $_.Exception.Message -like "*integridad*" -or $_.Exception.Message -like "*manifest*"
    }
    Assert-True $tamperRejected "El build no rechazó el payload manipulado antes de compilar."
    Add-Pass "tamper-detection" "un payload distinto de su manifest se rechaza antes de ISCC"

    Invoke-Setup $installerA "clean-install" $true | Out-Null
    Assert-ActiveVersion $payloadA
    Assert-UnrelatedSentinels $unrelatedSentinelHashes
    Add-Pass "clean-install" "payload A versionado, registro y acceso directo desechables"

    $installedA = Join-Path $appRoot "versions\$($payloadA.Version)\Trazio.AsistenteReunion.exe"
    [System.IO.File]::WriteAllText($installedA, "damaged")
    Invoke-Setup $installerA "same-version-repair" $true | Out-Null
    Assert-ActiveVersion $payloadA
    Assert-UnrelatedSentinels $unrelatedSentinelHashes
    Add-Pass "same-version-repair" "el archivo alterado fue restaurado"

    Invoke-Setup $installerB "upgrade-a-to-b" $true | Out-Null
    Assert-ActiveVersion $payloadB
    Assert-True (Test-Path -LiteralPath $installedA -PathType Leaf) "La actualización eliminó el payload anterior."
    Assert-UnrelatedSentinels $unrelatedSentinelHashes
    Add-Pass "upgrade-a-to-b" "B quedó activo y A permaneció disponible"

    Invoke-Setup $installerA "downgrade-b-to-a" $false | Out-Null
    Assert-ActiveVersion $payloadB
    Assert-UnrelatedSentinels $unrelatedSentinelHashes
    Add-Pass "downgrade-rejected" "B siguió activo después del rechazo de A"

    Set-ModernInstallerState -Version $payloadB.Version -Sequence 0
    Invoke-Setup $installerB "modern-sequence-zero" $false | Out-Null
    Set-ModernInstallerState -Version $payloadB.Version -Sequence $payloadB.Sequence
    Assert-ActiveVersion $payloadB
    Add-Pass "modern-sequence-zero-rejected" "la secuencia moderna 0 se rechazó antes de copiar archivos"

    Set-ModernInstallerState -Version $payloadB.Version -Sequence $payloadA.Sequence
    Invoke-Setup $installerB "modern-version-sequence-mismatch" $false | Out-Null
    Set-ModernInstallerState -Version $payloadB.Version -Sequence $payloadB.Sequence
    Assert-ActiveVersion $payloadB
    Add-Pass "modern-version-sequence-mismatch-rejected" "la versión B con secuencia A se rechazó antes de copiar archivos"

    Remove-ItemProperty -LiteralPath $installerRegistryPath -Name "ReleaseSequence"
    Invoke-Setup $installerB "modern-state-missing-sequence" $false | Out-Null
    Set-ModernInstallerState -Version $payloadB.Version -Sequence $payloadB.Sequence
    Remove-ItemProperty -LiteralPath $installerRegistryPath -Name "Version"
    Invoke-Setup $installerB "modern-state-missing-version" $false | Out-Null
    Set-ModernInstallerState -Version $payloadB.Version -Sequence $payloadB.Sequence
    Assert-ActiveVersion $payloadB
    Add-Pass "modern-state-incomplete-rejected" "la ausencia de Version o ReleaseSequence se rechazó antes de copiar archivos"

    Remove-Item -LiteralPath $installerRegistryPath -Recurse -Force
    Set-ItemProperty -LiteralPath $uninstallRegistryPath -Name "DisplayVersion" -Value "legacy-unknown"
    Invoke-Setup $installerB "unknown-legacy-version" $false | Out-Null
    Assert-True (-not (Test-Path -LiteralPath $installerRegistryPath)) "La ruta legacy desconocida creó estado moderno antes de copiar archivos."
    Set-ItemProperty -LiteralPath $uninstallRegistryPath -Name "DisplayVersion" -Value $payloadB.Version
    Set-ModernInstallerState -Version $payloadB.Version -Sequence $payloadB.Sequence
    Assert-ActiveVersion $payloadB
    Add-Pass "unknown-legacy-rejected" "la versión legacy desconocida no modificó el payload activo"

    $mutexProcess = Start-MutexHolder
    Invoke-Setup $installerB "running-app-block" $false | Out-Null
    $mutexProcess.Refresh()
    Assert-True (-not $mutexProcess.HasExited) "El proceso que retenía AppMutex terminó durante la prueba; no se demostró bloqueo sin cierre."
    Stop-Process -Id $mutexProcess.Id -Force
    $mutexProcess.WaitForExit(5000) | Out-Null
    $mutexProcess = $null
    Assert-ActiveVersion $payloadB
    Add-Pass "running-app-block" "AppMutex bloqueó la reparación sin cerrar procesos"

    $payloadCTarget = Join-Path $appRoot "versions\$($payloadC.Version)"
    $lockedTarget = Join-Path $payloadCTarget "zz-rollback-trigger.bin"
    New-Item -ItemType Directory -Force $payloadCTarget | Out-Null
    [System.IO.File]::WriteAllText($lockedTarget, "preexisting-lock", [System.Text.UTF8Encoding]::new($false))
    $lockedTargetHash = (Get-FileHash -LiteralPath $lockedTarget -Algorithm SHA256).Hash
    $lockedStream = [System.IO.File]::Open($lockedTarget, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
    try {
        $rollbackExitCode = Invoke-Setup $installerC "locked-copy-failure" $false
    }
    finally {
        $lockedStream.Dispose()
    }
    Assert-True ($rollbackExitCode -eq 5) "El Abort durante la instalación devolvió $rollbackExitCode en vez del código 5 documentado por Inno Setup."
    Assert-ActiveVersion $payloadB
    Assert-True ((Get-FileHash -LiteralPath $lockedTarget -Algorithm SHA256).Hash -eq $lockedTargetHash) "El fallo alteró el archivo preexistente bloqueado."
    $unexpectedCFiles = @(Get-ChildItem -LiteralPath $payloadCTarget -File -Recurse | Where-Object FullName -ne $lockedTarget)
    Assert-True ($unexpectedCFiles.Count -eq 0) "El rollback dejó archivos parciales del payload C."
    Remove-Item -LiteralPath $lockedTarget -Force
    Remove-Item -LiteralPath $payloadCTarget -Force
    Assert-UnrelatedSentinels $unrelatedSentinelHashes
    Add-Pass "transactional-rollback" "Abort durante [Files] devolvió 5; B siguió activo y C se retiró"

    $uninstaller = Join-Path $appRoot "unins000.exe"
    Assert-True (Test-Path -LiteralPath $uninstaller -PathType Leaf) "No existe el desinstalador desechable."
    $uninstallProcess = Start-Process -FilePath $uninstaller -ArgumentList @(
        "/VERYSILENT",
        "/SUPPRESSMSGBOXES",
        "/NORESTART") -Wait -PassThru
    Assert-True ($uninstallProcess.ExitCode -eq 0) "La desinstalación falló con código $($uninstallProcess.ExitCode)."
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $appRoot "versions"))) "La desinstalación dejó payloads versionados."
    Assert-True (-not (Test-Path -LiteralPath $installerRegistryPath)) "La desinstalación dejó el registro de activación."
    Assert-True (-not (Test-Path -LiteralPath $startMenuShortcutPath)) "La desinstalación dejó el acceso directo desechable del menú Inicio."
    Assert-True (-not (Test-Path -LiteralPath $desktopShortcutPath)) "La desinstalación dejó el acceso directo desechable del escritorio."
    Assert-ProductDesktopShortcutPreserved
    Assert-UnrelatedSentinels $unrelatedSentinelHashes
    Add-Pass "uninstall" "se retiró el programa desechable y permanecieron intactos archivos arbitrarios fuera de su árbol"

    $scenarioResults.Add([ordered]@{
        scenario = "interactive-cancellation"
        status = "NOT_AUTOMATED"
        evidence = "La cancelación humana requiere interacción real. El harness solo valida el rollback por Abort ante un fallo de copia durante [Files]."
    })
    Write-Warning "La cancelación interactiva no se automatiza ni se presenta como evidencia. El harness valida por separado el rollback ante un Abort durante [Files]."

    Write-Host "INSTALLER_HARNESS_OK: $(@($scenarioResults | Where-Object status -eq 'PASS').Count) escenarios PASS; cancelación interactiva NOT_AUTOMATED."
}
finally {
    Remove-HarnessState
    Assert-True (-not (Test-Path -LiteralPath $startMenuShortcutPath)) "La limpieza final dejó el acceso directo desechable del menú Inicio."
    Assert-True (-not (Test-Path -LiteralPath $desktopShortcutPath)) "La limpieza final dejó el acceso directo desechable del escritorio."
    Assert-ProductDesktopShortcutPreserved
}
