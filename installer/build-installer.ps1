param(
    [string]$Configuration = "Release",
    [switch]$SkipPublish,
    [string]$Version,
    [int]$ReleaseSequence = 0,
    [string]$PayloadDirectory,
    [string]$PayloadManifestPath,
    [string]$OutputDirectory,
    [string]$OutputBaseFilename,
    [string]$TestWorkspaceRoot,
    [string]$IsccPath,
    [switch]$AllowTestOverrides,
    [string]$AppGuid = "8C7AF6E0-31F1-4E2E-BF21-8A9AC6D1DDF1",
    [string]$DefaultDirName = "{localappdata}\Programs\Trazio Asistente Reunion",
    [string]$StartMenuGroup = "Trazio Asistente Reunión",
    [string]$ShortcutName = "Trazio Asistente Reunión",
    [string]$InstallerRegistrySubkey = "Software\Trazio\AsistenteReunion\Installer",
    [string]$AppMutex = "Trazio.AsistenteReunion.AppRunning.v1",
    [string]$SetupMutex = "Trazio.AsistenteReunion.Setup.v1"
)

$ErrorActionPreference = "Stop"
$explicitParameterNames = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
foreach ($parameterName in $PSBoundParameters.Keys) {
    $explicitParameterNames.Add($parameterName) | Out-Null
}
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root "artifacts"
$issPath = Join-Path $PSScriptRoot "Trazio.AsistenteReunion.iss"
$productAppGuid = "8C7AF6E0-31F1-4E2E-BF21-8A9AC6D1DDF1"
$productDefaultDirName = "{localappdata}\Programs\Trazio Asistente Reunion"
$productStartMenuGroup = "Trazio Asistente Reunión"
$productShortcutName = "Trazio Asistente Reunión"
$productInstallerRegistrySubkey = "Software\Trazio\AsistenteReunion\Installer"
$productAppMutex = "Trazio.AsistenteReunion.AppRunning.v1"
$productSetupMutex = "Trazio.AsistenteReunion.Setup.v1"
$canonicalPayloadDirectory = [System.IO.Path]::GetFullPath((Join-Path $artifacts "publish"))
$canonicalPayloadManifestPath = [System.IO.Path]::GetFullPath((Join-Path $artifacts "publish-manifest.json"))
$canonicalOutputDirectory = [System.IO.Path]::GetFullPath((Join-Path $artifacts "installer"))

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

function Get-InnoCompiler {
    param([string]$RequestedPath)

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        if (-not (Test-Path -LiteralPath $RequestedPath -PathType Leaf)) {
            throw "El ISCC solicitado no existe: $RequestedPath"
        }
        return [System.IO.Path]::GetFullPath($RequestedPath)
    }

    $candidates = [System.Collections.Generic.List[string]]::new()
    $command = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
    if ($command) {
        $candidates.Add($command.Source)
    }
    if ($env:LOCALAPPDATA) {
        $candidates.Add((Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"))
    }
    $programFilesX86 = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)
    if ($programFilesX86) {
        $candidates.Add((Join-Path $programFilesX86 "Inno Setup 6\ISCC.exe"))
    }
    $programFiles = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
    if ($programFiles) {
        $candidates.Add((Join-Path $programFiles "Inno Setup 6\ISCC.exe"))
    }

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return [System.IO.Path]::GetFullPath($candidate)
        }
    }
    throw "No se encontró ISCC.exe. Instala Inno Setup 6 o usa -IsccPath."
}

function Test-OverlappingIdentity {
    param([string]$Candidate, [string]$Product)

    $candidateNormalized = $Candidate.Trim().Replace('/', '\').TrimEnd('\')
    $productNormalized = $Product.Trim().Replace('/', '\').TrimEnd('\')
    return $candidateNormalized.Equals($productNormalized, [System.StringComparison]::OrdinalIgnoreCase) -or
        $candidateNormalized.StartsWith($productNormalized + '\', [System.StringComparison]::OrdinalIgnoreCase) -or
        $productNormalized.StartsWith($candidateNormalized + '\', [System.StringComparison]::OrdinalIgnoreCase)
}

function Test-PathWithinRoot {
    param([string]$Candidate, [string]$AllowedRoot)

    if ([string]::IsNullOrWhiteSpace($Candidate) -or
        [string]::IsNullOrWhiteSpace($AllowedRoot) -or
        -not [System.IO.Path]::IsPathRooted($Candidate) -or
        -not [System.IO.Path]::IsPathRooted($AllowedRoot)) {
        return $false
    }
    $rootFullPath = [System.IO.Path]::GetFullPath($AllowedRoot).TrimEnd('\', '/')
    $candidateFullPath = [System.IO.Path]::GetFullPath($Candidate).TrimEnd('\', '/')
    return $candidateFullPath.Equals($rootFullPath, [System.StringComparison]::OrdinalIgnoreCase) -or
        $candidateFullPath.StartsWith($rootFullPath + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)
}

function Test-PathWithinTemp {
    param([string]$Candidate)

    if ([string]::IsNullOrWhiteSpace($Candidate) -or -not [System.IO.Path]::IsPathRooted($Candidate)) {
        return $false
    }
    $tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\', '/')
    $candidateFullPath = [System.IO.Path]::GetFullPath($Candidate).TrimEnd('\', '/')
    return $candidateFullPath.StartsWith($tempRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)
}

function Assert-BuildMode {
    if ($AppGuid -notmatch '^[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}$') {
        throw "AppGuid no es un GUID válido."
    }
    foreach ($namedMutex in @($AppMutex, $SetupMutex)) {
        if ($namedMutex -notmatch '^[A-Za-z0-9._-]+$') {
            throw "El nombre de mutex contiene caracteres no permitidos: $namedMutex"
        }
    }
    foreach ($textValue in @($DefaultDirName, $StartMenuGroup, $ShortcutName, $InstallerRegistrySubkey)) {
        if ([string]::IsNullOrWhiteSpace($textValue) -or $textValue.Contains('"') -or $textValue.Contains("'") -or $textValue.Contains([char]10) -or $textValue.Contains([char]13)) {
            throw "Un parámetro de texto del instalador no es seguro."
        }
    }

    $overrideRequested = $Configuration -ne "Release"
    foreach ($parameterName in @(
        "SkipPublish", "Version", "ReleaseSequence", "PayloadDirectory", "PayloadManifestPath",
        "OutputDirectory", "OutputBaseFilename", "TestWorkspaceRoot", "AllowTestOverrides", "AppGuid", "DefaultDirName",
        "StartMenuGroup", "ShortcutName", "InstallerRegistrySubkey", "AppMutex", "SetupMutex")) {
        if ($explicitParameterNames.Contains($parameterName)) {
            $overrideRequested = $true
        }
    }

    if ($overrideRequested -and -not $AllowTestOverrides) {
        throw "Los parámetros de payload, versión, salida o identidad solo se aceptan con -AllowTestOverrides y un entorno completamente desechable."
    }

    if (-not $AllowTestOverrides) {
        $productValuesMatch =
            $Configuration -ceq "Release" -and
            -not $SkipPublish -and
            $Version -ceq $canonicalVersion -and
            $ReleaseSequence -eq $canonicalReleaseSequence -and
            $PayloadDirectory -ceq $canonicalPayloadDirectory -and
            $PayloadManifestPath -ceq $canonicalPayloadManifestPath -and
            $OutputDirectory -ceq $canonicalOutputDirectory -and
            $OutputBaseFilename -ceq "Trazio-Asistente-Reunion-v$canonicalVersion-Setup" -and
            $AppGuid -ceq $productAppGuid -and
            $DefaultDirName -ceq $productDefaultDirName -and
            $StartMenuGroup -ceq $productStartMenuGroup -and
            $ShortcutName -ceq $productShortcutName -and
            $InstallerRegistrySubkey -ceq $productInstallerRegistrySubkey -and
            $AppMutex -ceq $productAppMutex -and
            $SetupMutex -ceq $productSetupMutex
        if (-not $productValuesMatch) {
            throw "El build productivo exige la versión, identidades, salida y payload canónicos; publish.ps1 debe ejecutarse en esta misma invocación."
        }
        return $false
    }

    $requiredTestParameters = @(
        "SkipPublish", "Version", "ReleaseSequence", "PayloadDirectory", "PayloadManifestPath",
        "OutputDirectory", "OutputBaseFilename", "TestWorkspaceRoot", "AllowTestOverrides", "AppGuid",
        "DefaultDirName", "StartMenuGroup", "ShortcutName", "InstallerRegistrySubkey", "AppMutex", "SetupMutex")
    $missingTestParameters = @($requiredTestParameters | Where-Object { -not $explicitParameterNames.Contains($_) })
    if (-not $SkipPublish -or $missingTestParameters.Count -gt 0) {
        throw "El modo desechable exige -SkipPublish y todos los parámetros explícitos de identidad, versión, payload, salida y TestWorkspaceRoot. Faltan: $($missingTestParameters -join ', ')."
    }
    if (-not (Test-PathWithinTemp $TestWorkspaceRoot)) {
        throw "TestWorkspaceRoot debe ser una ruta absoluta y desechable bajo TEMP."
    }

    $testWorkspaceLeaf = [System.IO.Path]::GetFileName($TestWorkspaceRoot.TrimEnd('\', '/'))
    $runIdMatch = [System.Text.RegularExpressions.Regex]::Match($testWorkspaceLeaf, '(?i)(?<runId>[0-9a-f]{32})$')

    $unsafeIdentities = @()
    if ($AppGuid.Equals($productAppGuid, [System.StringComparison]::OrdinalIgnoreCase)) { $unsafeIdentities += "AppGuid" }
    if (-not (Test-PathWithinRoot -Candidate $DefaultDirName -AllowedRoot $TestWorkspaceRoot)) { $unsafeIdentities += "DefaultDirName" }
    if (Test-OverlappingIdentity $StartMenuGroup $productStartMenuGroup) { $unsafeIdentities += "StartMenuGroup" }
    if (Test-OverlappingIdentity $ShortcutName $productShortcutName) { $unsafeIdentities += "ShortcutName" }
    if (-not $runIdMatch.Success -or $ShortcutName.IndexOf($runIdMatch.Groups['runId'].Value, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) { $unsafeIdentities += "ShortcutNameRunId" }
    if (Test-OverlappingIdentity $InstallerRegistrySubkey $productInstallerRegistrySubkey) { $unsafeIdentities += "InstallerRegistrySubkey" }
    if ($AppMutex.Equals($productAppMutex, [System.StringComparison]::OrdinalIgnoreCase)) { $unsafeIdentities += "AppMutex" }
    if ($SetupMutex.Equals($productSetupMutex, [System.StringComparison]::OrdinalIgnoreCase)) { $unsafeIdentities += "SetupMutex" }
    if (-not (Test-PathWithinRoot -Candidate $PayloadDirectory -AllowedRoot $TestWorkspaceRoot)) { $unsafeIdentities += "PayloadDirectory" }
    if (-not (Test-PathWithinRoot -Candidate $PayloadManifestPath -AllowedRoot $TestWorkspaceRoot)) { $unsafeIdentities += "PayloadManifestPath" }
    if (-not (Test-PathWithinRoot -Candidate $OutputDirectory -AllowedRoot $TestWorkspaceRoot)) { $unsafeIdentities += "OutputDirectory" }
    if ($OutputBaseFilename.Equals("Trazio-Asistente-Reunion-v$canonicalVersion-Setup", [System.StringComparison]::OrdinalIgnoreCase)) { $unsafeIdentities += "OutputBaseFilename" }
    if ($unsafeIdentities.Count -gt 0) {
        throw "El modo desechable prohíbe mezclar identidades productivas o rutas fuera de TestWorkspaceRoot: $($unsafeIdentities -join ', ')."
    }
    return $true
}

function Assert-PayloadManifest {
    param(
        [string]$Directory,
        [string]$ManifestPath,
        [string]$ExpectedVersion,
        [int]$ExpectedSequence
    )

    if (-not (Test-Path -LiteralPath $Directory -PathType Container)) {
        throw "No existe el payload: $Directory"
    }
    if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
        throw "No existe el manifest del payload: $ManifestPath"
    }
    try {
        $manifest = Read-Utf8Text $ManifestPath | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        throw "El manifest del payload no es JSON válido: $($_.Exception.Message)"
    }
    if ($manifest.schemaVersion -ne 1 -or
        $manifest.product -ne "Trazio Asistente Reunión" -or
        $manifest.version -ne $ExpectedVersion -or
        $manifest.releaseSequence -ne $ExpectedSequence) {
        throw "El manifest del payload no coincide con producto, versión o secuencia."
    }

    $actualRelativePaths = [string[]]@(
        Get-ChildItem -LiteralPath $Directory -File -Recurse | ForEach-Object {
            Get-NormalizedRelativePath -BasePath $Directory -FullPath $_.FullName
        }
    )
    [Array]::Sort($actualRelativePaths, [System.StringComparer]::Ordinal)
    $entries = @($manifest.files)
    if ($entries.Count -ne $actualRelativePaths.Count) {
        throw "El manifest declara $($entries.Count) archivos, pero el payload contiene $($actualRelativePaths.Count)."
    }

    $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    for ($index = 0; $index -lt $entries.Count; $index++) {
        $entry = $entries[$index]
        $relativePath = [string]$entry.path
        if ([string]::IsNullOrWhiteSpace($relativePath) -or
            [System.IO.Path]::IsPathRooted($relativePath) -or
            $relativePath.Contains('\') -or
            $relativePath.Split('/').Contains('..') -or
            -not $seen.Add($relativePath)) {
            throw "El manifest contiene una ruta insegura o duplicada: $relativePath"
        }
        if ($relativePath -cne $actualRelativePaths[$index]) {
            throw "El manifest no está ordenado o no coincide con el payload en: $relativePath"
        }
        $fullPath = Join-Path $Directory ($relativePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
        $file = Get-Item -LiteralPath $fullPath
        $actualHash = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ([long]$entry.length -ne [long]$file.Length -or [string]$entry.sha256 -cne $actualHash) {
            throw "Falló la integridad del payload para: $relativePath"
        }
    }
}

function Assert-InstallerArtifacts {
    param(
        [string]$InstallerPath,
        [string]$ChecksumPath,
        [string]$ManifestPath,
        [string]$PayloadManifestPath,
        [string]$ExpectedVersion,
        [int]$ExpectedSequence
    )

    foreach ($requiredPath in @($InstallerPath, $ChecksumPath, $ManifestPath, $PayloadManifestPath)) {
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
            throw "Falta un artefacto obligatorio del instalador: $requiredPath"
        }
    }

    $manifestBytes = [System.IO.File]::ReadAllBytes($ManifestPath)
    if ($manifestBytes.Length -eq 0 -or
        ($manifestBytes.Length -ge 3 -and $manifestBytes[0] -eq 0xEF -and $manifestBytes[1] -eq 0xBB -and $manifestBytes[2] -eq 0xBF) -or
        ($manifestBytes -contains 13) -or
        $manifestBytes[$manifestBytes.Length - 1] -ne 10) {
        throw "El manifest del instalador debe ser UTF-8 sin BOM, usar solo LF y terminar en LF."
    }
    try {
        $manifest = Read-Utf8Text $ManifestPath | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        throw "El manifest del instalador no es JSON válido: $($_.Exception.Message)"
    }
    $expectedFields = @("schemaVersion", "product", "version", "releaseSequence", "file", "length", "sha256", "payloadManifest", "payloadManifestSha256", "authenticode")
    $actualFields = @($manifest.PSObject.Properties.Name)
    if (($actualFields -join '|') -cne ($expectedFields -join '|')) {
        throw "El manifest del instalador no contiene exactamente los campos esperados y en orden."
    }

    $installerFile = Get-Item -LiteralPath $InstallerPath
    $installerHash = (Get-FileHash -LiteralPath $InstallerPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $payloadManifestHash = (Get-FileHash -LiteralPath $PayloadManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ([int]$manifest.schemaVersion -ne 1 -or
        [string]$manifest.product -cne "Trazio Asistente Reunión" -or
        [string]$manifest.version -cne $ExpectedVersion -or
        [int]$manifest.releaseSequence -ne $ExpectedSequence -or
        [string]$manifest.file -cne $installerFile.Name -or
        [long]$manifest.length -ne [long]$installerFile.Length -or
        [string]$manifest.sha256 -cnotmatch '^[0-9a-f]{64}$' -or
        [string]$manifest.sha256 -cne $installerHash -or
        [string]$manifest.payloadManifest -cne [System.IO.Path]::GetFileName($PayloadManifestPath) -or
        [string]$manifest.payloadManifestSha256 -cnotmatch '^[0-9a-f]{64}$' -or
        [string]$manifest.payloadManifestSha256 -cne $payloadManifestHash -or
        [string]$manifest.authenticode -cne "unsigned") {
        throw "El manifest del instalador no coincide exactamente con el binario, payload, versión o formato esperado."
    }
    if ((Get-AuthenticodeSignature -LiteralPath $InstallerPath).Status -ne [System.Management.Automation.SignatureStatus]::NotSigned) {
        throw "El estado Authenticode real no coincide con el manifest del instalador."
    }

    $expectedChecksum = "$installerHash  $($installerFile.Name)" + [char]10
    if ((Read-Utf8Text $ChecksumPath) -cne $expectedChecksum) {
        throw "El sidecar SHA-256 no contiene exactamente el hash, nombre y formato canónicos."
    }
}

[xml]$buildProperties = Read-Utf8Text (Join-Path $root "Directory.Build.props")
$sharedProperties = @($buildProperties.Project.PropertyGroup)[0]
$canonicalVersion = [string]$sharedProperties.Version
$canonicalReleaseSequence = [int]$sharedProperties.InstallerReleaseSequence
if ($explicitParameterNames.Contains("Version") -and [string]::IsNullOrWhiteSpace($Version)) {
    throw "La versión explícita no puede estar vacía."
}
if ($explicitParameterNames.Contains("ReleaseSequence") -and $ReleaseSequence -lt 1) {
    throw "La secuencia explícita debe ser mayor o igual que 1."
}
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = $canonicalVersion
}
if ($ReleaseSequence -eq 0) {
    $ReleaseSequence = $canonicalReleaseSequence
}
if ($Version -notmatch '^[0-9A-Za-z][0-9A-Za-z.-]*$' -or $ReleaseSequence -lt 1) {
    throw "Versión o secuencia de instalador no válida."
}

if ([string]::IsNullOrWhiteSpace($PayloadDirectory)) {
    $PayloadDirectory = Join-Path $artifacts "publish"
}
if ([string]::IsNullOrWhiteSpace($PayloadManifestPath)) {
    $PayloadManifestPath = Join-Path $artifacts "publish-manifest.json"
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $artifacts "installer"
}
if ([string]::IsNullOrWhiteSpace($OutputBaseFilename)) {
    $OutputBaseFilename = "Trazio-Asistente-Reunion-v$Version-Setup"
}
if ($OutputBaseFilename -notmatch '^[0-9A-Za-z._-]+$') {
    throw "OutputBaseFilename contiene caracteres no permitidos."
}

$PayloadDirectory = [System.IO.Path]::GetFullPath($PayloadDirectory)
$PayloadManifestPath = [System.IO.Path]::GetFullPath($PayloadManifestPath)
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
if (-not [string]::IsNullOrWhiteSpace($TestWorkspaceRoot)) {
    $TestWorkspaceRoot = [System.IO.Path]::GetFullPath($TestWorkspaceRoot)
}
$isTestOverrideMode = Assert-BuildMode

if (-not $isTestOverrideMode) {
    & (Join-Path $PSScriptRoot "publish.ps1") -Configuration $Configuration
    if ($LASTEXITCODE -ne 0) {
        throw "La publicación falló con código $LASTEXITCODE."
    }
}
Assert-PayloadManifest -Directory $PayloadDirectory -ManifestPath $PayloadManifestPath -ExpectedVersion $Version -ExpectedSequence $ReleaseSequence

$compiler = Get-InnoCompiler -RequestedPath $IsccPath
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$installerPath = Join-Path $OutputDirectory "$OutputBaseFilename.exe"
$checksumPath = "$installerPath.sha256"
$installerManifestPath = "$installerPath.manifest.json"
Remove-Item -LiteralPath $installerPath, $checksumPath, $installerManifestPath -Force -ErrorAction SilentlyContinue

$defines = @(
    "/DMyAppVersion=$Version",
    "/DMyReleaseSequence=$ReleaseSequence",
    "/DMyAppGuid=$AppGuid",
    "/DMyPayloadDir=$PayloadDirectory",
    "/DMyPayloadManifest=$PayloadManifestPath",
    "/DMyOutputDir=$OutputDirectory",
    "/DMyOutputBaseFilename=$OutputBaseFilename",
    "/DMyDefaultDirName=$DefaultDirName",
    "/DMyStartMenuGroup=$StartMenuGroup",
    "/DMyShortcutName=$ShortcutName",
    "/DMyInstallerRegistrySubkey=$InstallerRegistrySubkey",
    "/DMyAppMutex=$AppMutex",
    "/DMySetupMutex=$SetupMutex"
)

& $compiler @defines $issPath
if ($LASTEXITCODE -ne 0) {
    throw "ISCC falló con código $LASTEXITCODE."
}
if (-not (Test-Path -LiteralPath $installerPath -PathType Leaf)) {
    throw "ISCC no generó el instalador esperado: $installerPath"
}

$signature = Get-AuthenticodeSignature -LiteralPath $installerPath
if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::NotSigned) {
    throw "Este flujo espera un instalador sin firma, pero Authenticode informó: $($signature.Status)"
}
$installerFile = Get-Item -LiteralPath $installerPath
$installerHash = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash.ToLowerInvariant()
$payloadManifestHash = (Get-FileHash -LiteralPath $PayloadManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
$checksumLine = "$installerHash  $($installerFile.Name)" + [char]10
[System.IO.File]::WriteAllText($checksumPath, $checksumLine, [System.Text.UTF8Encoding]::new($false))

$installerManifest = [ordered]@{
    schemaVersion = 1
    product = "Trazio Asistente Reunión"
    version = $Version
    releaseSequence = $ReleaseSequence
    file = $installerFile.Name
    length = [long]$installerFile.Length
    sha256 = $installerHash
    payloadManifest = [System.IO.Path]::GetFileName($PayloadManifestPath)
    payloadManifestSha256 = $payloadManifestHash
    authenticode = "unsigned"
}
$installerManifestJson = ($installerManifest | ConvertTo-Json -Depth 4).Replace(
    [Environment]::NewLine,
    [string][char]10) + [char]10
[System.IO.File]::WriteAllText(
    $installerManifestPath,
    $installerManifestJson,
    [System.Text.UTF8Encoding]::new($false))

Assert-InstallerArtifacts `
    -InstallerPath $installerPath `
    -ChecksumPath $checksumPath `
    -ManifestPath $installerManifestPath `
    -PayloadManifestPath $PayloadManifestPath `
    -ExpectedVersion $Version `
    -ExpectedSequence $ReleaseSequence

Write-Host "Instalador: $installerPath"
Write-Host "SHA-256: $installerHash"
Write-Host "Sidecars: $checksumPath ; $installerManifestPath"
