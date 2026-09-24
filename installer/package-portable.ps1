param(
    [string]$PayloadDirectory,
    [string]$PayloadManifestPath,
    [string]$OutputDirectory,
    [string]$ArchiveName,
    [switch]$AllowTestOverrides,
    [string]$TestWorkspaceRoot,
    [ValidateSet("None", "BeforeCommit", "AfterArchiveReplace", "DuringRollback")]
    [string]$InjectTestFailure = "None"
)

$ErrorActionPreference = "Stop"
$explicitParameterNames = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
foreach ($parameterName in $PSBoundParameters.Keys) {
    $explicitParameterNames.Add($parameterName) | Out-Null
}

if ($PSVersionTable.PSEdition -ne "Core" -or $PSVersionTable.PSVersion -lt [Version]"7.4") {
    throw "package-portable.ps1 requiere PowerShell 7.4 o posterior; Windows PowerShell 5.1 no está admitido."
}
if ([System.Environment]::Version.Major -lt 8) {
    throw "package-portable.ps1 requiere un runtime .NET 8 o posterior."
}

$root = Split-Path -Parent $PSScriptRoot
$artifacts = [System.IO.Path]::GetFullPath((Join-Path $root "artifacts"))
$canonicalPayloadDirectory = [System.IO.Path]::GetFullPath((Join-Path $artifacts "publish"))
$canonicalPayloadManifestPath = [System.IO.Path]::GetFullPath((Join-Path $artifacts "publish-manifest.json"))

function Read-Utf8Text {
    param([string]$Path)

    $bytes = [System.IO.File]::ReadAllBytes([System.IO.Path]::GetFullPath($Path))
    return [System.Text.UTF8Encoding]::new($false, $true).GetString($bytes)
}

function Read-CanonicalManifestText {
    param([string]$Path)

    $bytes = [System.IO.File]::ReadAllBytes([System.IO.Path]::GetFullPath($Path))
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
        throw "El manifiesto no debe contener BOM UTF-8: $Path"
    }
    if ($bytes.Length -eq 0 -or $bytes[$bytes.Length - 1] -ne 10 -or $bytes -contains 13) {
        throw "El manifiesto debe usar UTF-8 sin BOM, solo LF y terminar en LF: $Path"
    }
    return [System.Text.UTF8Encoding]::new($false, $true).GetString($bytes)
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
        $candidateFullPath.StartsWith(
            $rootFullPath + [System.IO.Path]::DirectorySeparatorChar,
            [System.StringComparison]::OrdinalIgnoreCase)
}

function Test-PathWithinTemp {
    param([string]$Candidate)

    if ([string]::IsNullOrWhiteSpace($Candidate) -or -not [System.IO.Path]::IsPathRooted($Candidate)) {
        return $false
    }

    $tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\', '/')
    $candidateFullPath = [System.IO.Path]::GetFullPath($Candidate).TrimEnd('\', '/')
    return $candidateFullPath.StartsWith(
        $tempRoot + [System.IO.Path]::DirectorySeparatorChar,
        [System.StringComparison]::OrdinalIgnoreCase)
}

function Assert-NoReparsePoint {
    param([string]$Path, [string]$Description)

    $attributes = [System.IO.File]::GetAttributes([System.IO.Path]::GetFullPath($Path))
    if (($attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$Description no puede ser un vínculo simbólico, junction ni otro reparse point: $Path"
    }
}

function Assert-AncestorsAreNotReparsePoints {
    param([string]$Path, [string]$StopAt)

    $stopFullPath = [System.IO.Path]::GetFullPath($StopAt).TrimEnd('\', '/')
    $current = [System.IO.DirectoryInfo]::new([System.IO.Path]::GetFullPath($Path))
    while ($null -ne $current) {
        Assert-NoReparsePoint -Path $current.FullName -Description "Una ruta de trabajo"
        if ($current.FullName.TrimEnd('\', '/').Equals($stopFullPath, [System.StringComparison]::OrdinalIgnoreCase)) {
            return
        }
        $current = $current.Parent
    }
    throw "La ruta no desciende de la raíz permitida: $Path"
}

function Get-PayloadFiles {
    param([string]$PayloadRoot)

    $files = [System.Collections.Generic.List[System.IO.FileInfo]]::new()
    $pending = [System.Collections.Generic.Stack[System.IO.DirectoryInfo]]::new()
    $payloadDirectoryInfo = [System.IO.DirectoryInfo]::new([System.IO.Path]::GetFullPath($PayloadRoot))
    Assert-NoReparsePoint -Path $payloadDirectoryInfo.FullName -Description "La raíz del payload"
    $pending.Push($payloadDirectoryInfo)

    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        foreach ($entry in $directory.EnumerateFileSystemInfos("*", [System.IO.SearchOption]::TopDirectoryOnly)) {
            if (($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "El payload contiene un vínculo simbólico, junction u otro reparse point: $($entry.FullName)"
            }
            if (($entry.Attributes -band [System.IO.FileAttributes]::Directory) -ne 0) {
                $pending.Push([System.IO.DirectoryInfo]$entry)
            }
            else {
                $files.Add([System.IO.FileInfo]$entry)
            }
        }
    }
    return $files.ToArray()
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

function Assert-ExactProperties {
    param(
        [System.Text.Json.JsonElement]$Element,
        [string[]]$ExpectedNames,
        [string]$Description
    )

    if ($Element.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) {
        throw "$Description debe ser un objeto JSON."
    }
    $actualNames = @($Element.EnumerateObject() | ForEach-Object Name)
    if (($actualNames -join '|') -cne ($ExpectedNames -join '|')) {
        throw "$Description no contiene exactamente los campos esperados y en orden."
    }
}

function Assert-PortableRelativePath {
    param([string]$RelativePath)

    if ([string]::IsNullOrWhiteSpace($RelativePath) -or
        [System.IO.Path]::IsPathRooted($RelativePath) -or
        $RelativePath.Contains('\') -or
        $RelativePath.Contains(':') -or
        $RelativePath.StartsWith('/', [System.StringComparison]::Ordinal) -or
        $RelativePath.EndsWith('/', [System.StringComparison]::Ordinal) -or
        $RelativePath.Contains('//') -or
        -not $RelativePath.IsNormalized([System.Text.NormalizationForm]::FormC)) {
        throw "La ruta del manifiesto no es relativa y portable: $RelativePath"
    }

    $invalidCharacters = [char[]]'<>"|?*'
    $reservedNames = [System.Collections.Generic.HashSet[string]]::new(
        [string[]]@("CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"),
        [System.StringComparer]::OrdinalIgnoreCase)
    foreach ($segment in $RelativePath.Split('/')) {
        $baseName = $segment.Split('.')[0]
        $hasControlCharacter = $null -ne ($segment.ToCharArray() | Where-Object { [int]$_ -lt 32 } | Select-Object -First 1)
        if ([string]::IsNullOrWhiteSpace($segment) -or
            $segment -ceq "." -or
            $segment -ceq ".." -or
            $segment.EndsWith(" ", [System.StringComparison]::Ordinal) -or
            $segment.EndsWith(".", [System.StringComparison]::Ordinal) -or
            $segment.IndexOfAny($invalidCharacters) -ge 0 -or
            $hasControlCharacter -or
            $reservedNames.Contains($baseName)) {
            throw "La ruta del manifiesto contiene un segmento ambiguo o reservado: $RelativePath"
        }
    }
}

function Read-PayloadManifest {
    param([string]$ManifestPath)

    $manifestText = Read-CanonicalManifestText $ManifestPath
    $document = [System.Text.Json.JsonDocument]::Parse(
        $manifestText,
        [System.Text.Json.JsonDocumentOptions]@{
            AllowTrailingCommas = $false
            CommentHandling = [System.Text.Json.JsonCommentHandling]::Disallow
        })
    try {
        $manifestRoot = $document.RootElement
        Assert-ExactProperties -Element $manifestRoot -ExpectedNames @("schemaVersion", "product", "version", "releaseSequence", "files") -Description "El manifiesto"
        if ($manifestRoot.GetProperty("schemaVersion").ValueKind -ne [System.Text.Json.JsonValueKind]::Number -or
            $manifestRoot.GetProperty("schemaVersion").GetInt32() -ne 1 -or
            $manifestRoot.GetProperty("product").ValueKind -ne [System.Text.Json.JsonValueKind]::String -or
            $manifestRoot.GetProperty("product").GetString() -cne "Trazio Asistente Reunión" -or
            $manifestRoot.GetProperty("version").ValueKind -ne [System.Text.Json.JsonValueKind]::String -or
            [string]::IsNullOrWhiteSpace($manifestRoot.GetProperty("version").GetString()) -or
            $manifestRoot.GetProperty("releaseSequence").ValueKind -ne [System.Text.Json.JsonValueKind]::Number -or
            $manifestRoot.GetProperty("releaseSequence").GetInt32() -lt 1 -or
            $manifestRoot.GetProperty("files").ValueKind -ne [System.Text.Json.JsonValueKind]::Array) {
            throw "El manifiesto contiene metadatos inválidos."
        }

        $seenOrdinal = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
        $seenWindows = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
        $files = [System.Collections.Generic.List[object]]::new()
        $previousPath = $null
        foreach ($fileElement in $manifestRoot.GetProperty("files").EnumerateArray()) {
            Assert-ExactProperties -Element $fileElement -ExpectedNames @("path", "length", "sha256") -Description "Una entrada del manifiesto"
            if ($fileElement.GetProperty("path").ValueKind -ne [System.Text.Json.JsonValueKind]::String -or
                $fileElement.GetProperty("length").ValueKind -ne [System.Text.Json.JsonValueKind]::Number -or
                $fileElement.GetProperty("sha256").ValueKind -ne [System.Text.Json.JsonValueKind]::String) {
                throw "Una entrada del manifiesto tiene tipos inválidos."
            }
            $relativePath = $fileElement.GetProperty("path").GetString()
            $length = $fileElement.GetProperty("length").GetInt64()
            $sha256 = $fileElement.GetProperty("sha256").GetString()
            Assert-PortableRelativePath $relativePath
            if ($length -lt 0 -or $sha256 -cnotmatch '^[0-9a-f]{64}$') {
                throw "Una entrada del manifiesto contiene longitud o SHA-256 inválidos: $relativePath"
            }
            if (-not $seenOrdinal.Add($relativePath) -or -not $seenWindows.Add($relativePath)) {
                throw "El manifiesto contiene una ruta duplicada o ambigua en Windows: $relativePath"
            }
            if ($null -ne $previousPath -and [System.StringComparer]::Ordinal.Compare($previousPath, $relativePath) -ge 0) {
                throw "Las rutas del manifiesto no están en orden ordinal estricto."
            }
            $previousPath = $relativePath
            $files.Add([pscustomobject]@{ Path = $relativePath; Length = [long]$length; Sha256 = $sha256 })
        }
        if ($files.Count -eq 0) {
            throw "El manifiesto no contiene archivos."
        }
        return [pscustomobject]@{
            Version = $manifestRoot.GetProperty("version").GetString()
            ReleaseSequence = $manifestRoot.GetProperty("releaseSequence").GetInt32()
            Files = $files.ToArray()
        }
    }
    finally {
        $document.Dispose()
    }
}

function Get-FileSha256 {
    param([string]$Path)

    $stream = [System.IO.File]::Open(
        [System.IO.Path]::GetFullPath($Path),
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::Read)
    try {
        return [System.Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant()
    }
    finally {
        $stream.Dispose()
    }
}

function Assert-PayloadMatchesManifest {
    param([string]$PayloadRoot, [object]$Manifest)

    $payloadFiles = @(Get-PayloadFiles $PayloadRoot)
    $actualRelativePaths = [string[]]@(
        $payloadFiles | ForEach-Object { Get-NormalizedRelativePath -BasePath $PayloadRoot -FullPath $_.FullName }
    )
    [Array]::Sort($actualRelativePaths, [System.StringComparer]::Ordinal)
    $expectedRelativePaths = [string[]]@($Manifest.Files | ForEach-Object Path)
    if (($actualRelativePaths -join "`n") -cne ($expectedRelativePaths -join "`n")) {
        $missing = @($expectedRelativePaths | Where-Object { $_ -cnotin $actualRelativePaths })
        $extra = @($actualRelativePaths | Where-Object { $_ -cnotin $expectedRelativePaths })
        throw "El conjunto del payload no coincide exactamente con el manifiesto. Faltantes=[$($missing -join ', ')] Extras=[$($extra -join ', ')]"
    }

    foreach ($manifestFile in $Manifest.Files) {
        $fullPath = [System.IO.Path]::GetFullPath((Join-Path $PayloadRoot $manifestFile.Path.Replace('/', [System.IO.Path]::DirectorySeparatorChar)))
        if (-not (Test-PathWithinRoot -Candidate $fullPath -AllowedRoot $PayloadRoot)) {
            throw "Una ruta del manifiesto escapa del payload: $($manifestFile.Path)"
        }
        Assert-NoReparsePoint -Path $fullPath -Description "Un archivo del payload"
        $file = [System.IO.FileInfo]::new($fullPath)
        if ($file.Length -ne $manifestFile.Length) {
            throw "La longitud del payload no coincide con el manifiesto: $($manifestFile.Path)"
        }
        $actualHash = Get-FileSha256 $fullPath
        if ($actualHash -cne $manifestFile.Sha256) {
            throw "La integridad SHA-256 del payload no coincide con el manifiesto: $($manifestFile.Path)"
        }
    }
}

function New-DeterministicZip {
    param([string]$PayloadRoot, [object]$Manifest, [string]$DestinationPath)

    $fixedTimestamp = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
    $archiveStream = [System.IO.File]::Open(
        $DestinationPath,
        [System.IO.FileMode]::CreateNew,
        [System.IO.FileAccess]::ReadWrite,
        [System.IO.FileShare]::None)
    try {
        $archive = [System.IO.Compression.ZipArchive]::new(
            $archiveStream,
            [System.IO.Compression.ZipArchiveMode]::Create,
            $true,
            [System.Text.UTF8Encoding]::new($false))
        try {
            foreach ($manifestFile in $Manifest.Files) {
                $fullPath = [System.IO.Path]::GetFullPath((Join-Path $PayloadRoot $manifestFile.Path.Replace('/', [System.IO.Path]::DirectorySeparatorChar)))
                $source = [System.IO.File]::Open(
                    $fullPath,
                    [System.IO.FileMode]::Open,
                    [System.IO.FileAccess]::Read,
                    [System.IO.FileShare]::Read)
                try {
                    if ($source.Length -ne $manifestFile.Length) {
                        throw "El payload cambió después de validarlo: $($manifestFile.Path)"
                    }
                    $entry = $archive.CreateEntry($manifestFile.Path, [System.IO.Compression.CompressionLevel]::Optimal)
                    $entry.LastWriteTime = $fixedTimestamp
                    $entry.ExternalAttributes = 0
                    $entryStream = $entry.Open()
                    $hash = [System.Security.Cryptography.IncrementalHash]::CreateHash([System.Security.Cryptography.HashAlgorithmName]::SHA256)
                    try {
                        $buffer = [byte[]]::new(1MB)
                        $written = [long]0
                        while (($read = $source.Read($buffer, 0, $buffer.Length)) -gt 0) {
                            $entryStream.Write($buffer, 0, $read)
                            $hash.AppendData($buffer, 0, $read)
                            $written += $read
                        }
                        $archivedHash = [System.Convert]::ToHexString($hash.GetHashAndReset()).ToLowerInvariant()
                        if ($written -ne $manifestFile.Length -or $archivedHash -cne $manifestFile.Sha256) {
                            throw "El payload cambió mientras se archivaba: $($manifestFile.Path)"
                        }
                    }
                    finally {
                        $hash.Dispose()
                        $entryStream.Dispose()
                    }
                }
                finally {
                    $source.Dispose()
                }
            }
        }
        finally {
            $archive.Dispose()
        }
        $archiveStream.Flush($true)
    }
    finally {
        $archiveStream.Dispose()
    }
}

function Assert-ZipMatchesManifest {
    param([string]$ZipPath, [object]$Manifest)

    $expectedTimestamp = [DateTime]::new(2000, 1, 1, 0, 0, 0, [DateTimeKind]::Unspecified)
    $zipStream = [System.IO.File]::Open($ZipPath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
    try {
        $archive = [System.IO.Compression.ZipArchive]::new($zipStream, [System.IO.Compression.ZipArchiveMode]::Read, $false)
        try {
            $entries = @($archive.Entries)
            if ($entries.Count -ne $Manifest.Files.Count) {
                throw "El ZIP no contiene exactamente las entradas del manifiesto."
            }
            for ($index = 0; $index -lt $entries.Count; $index++) {
                $entry = $entries[$index]
                $manifestFile = $Manifest.Files[$index]
                if ($entry.FullName -cne $manifestFile.Path -or $entry.FullName.Contains('\') -or $entry.Length -ne $manifestFile.Length) {
                    throw "El orden, ruta o longitud del ZIP no coincide con el manifiesto: $($entry.FullName)"
                }
                if ($entry.LastWriteTime.DateTime -ne $expectedTimestamp -or $entry.ExternalAttributes -ne 0) {
                    throw "Los metadatos normalizados del ZIP no coinciden: $($entry.FullName)"
                }
                $entryStream = $entry.Open()
                try {
                    $entryHash = [System.Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($entryStream)).ToLowerInvariant()
                    if ($entryHash -cne $manifestFile.Sha256) {
                        throw "El contenido del ZIP no coincide con el manifiesto: $($entry.FullName)"
                    }
                }
                finally {
                    $entryStream.Dispose()
                }
            }
        }
        finally {
            $archive.Dispose()
        }
    }
    finally {
        $zipStream.Dispose()
    }
}

function Restore-Artifact {
    param([string]$Destination, [string]$Backup, [bool]$Existed)

    if ($Existed) {
        if (Test-Path -LiteralPath $Backup -PathType Leaf) {
            if (Test-Path -LiteralPath $Destination -PathType Leaf) {
                $failedArtifact = "$Backup.failed"
                try {
                    [System.IO.File]::Replace($Backup, $Destination, $failedArtifact, $true)
                }
                finally {
                    if (Test-Path -LiteralPath $failedArtifact -PathType Leaf) {
                        [System.IO.File]::Delete($failedArtifact)
                    }
                }
            }
            else {
                [System.IO.File]::Move($Backup, $Destination)
            }
        }
    }
    elseif (Test-Path -LiteralPath $Destination -PathType Leaf) {
        [System.IO.File]::Delete($Destination)
    }
}

function Install-Artifact {
    param([string]$Temporary, [string]$Destination, [string]$Backup, [bool]$Existed)

    if ($Existed) {
        [System.IO.File]::Replace($Temporary, $Destination, $Backup, $true)
    }
    else {
        [System.IO.File]::Move($Temporary, $Destination)
    }
}

function Assert-BuildMode {
    $overrideParameters = @("PayloadDirectory", "PayloadManifestPath", "OutputDirectory", "ArchiveName", "TestWorkspaceRoot", "AllowTestOverrides", "InjectTestFailure")
    if (-not $AllowTestOverrides) {
        $requestedOverrides = @($overrideParameters | Where-Object { $explicitParameterNames.Contains($_) })
        if ($requestedOverrides.Count -gt 0) {
            throw "El modo productivo no acepta rutas, nombres ni inyección personalizados. Usa la invocación sin parámetros."
        }
        return
    }

    foreach ($requiredOverride in @("PayloadDirectory", "PayloadManifestPath", "OutputDirectory", "ArchiveName", "TestWorkspaceRoot")) {
        if (-not $explicitParameterNames.Contains($requiredOverride)) {
            throw "El modo sintético exige todos los parámetros de ruta, nombre y TestWorkspaceRoot explícitos."
        }
    }
    if (-not (Test-PathWithinTemp $TestWorkspaceRoot)) {
        throw "TestWorkspaceRoot debe ser una subcarpeta absoluta de TEMP."
    }
    $testRootFullPath = [System.IO.Path]::GetFullPath($TestWorkspaceRoot)
    foreach ($testPath in @($PayloadDirectory, $PayloadManifestPath, $OutputDirectory)) {
        if (-not (Test-PathWithinRoot -Candidate $testPath -AllowedRoot $testRootFullPath)) {
            throw "Todas las rutas sintéticas deben permanecer dentro de TestWorkspaceRoot."
        }
    }
    if ([string]::IsNullOrWhiteSpace($ArchiveName) -or
        [System.IO.Path]::GetFileName($ArchiveName) -cne $ArchiveName -or
        -not $ArchiveName.EndsWith(".zip", [System.StringComparison]::OrdinalIgnoreCase) -or
        -not ($ArchiveName.StartsWith("synthetic-", [System.StringComparison]::Ordinal) -or
            $ArchiveName.StartsWith("test-", [System.StringComparison]::Ordinal))) {
        throw "El modo sintético exige un nombre ZIP simple que comience con synthetic- o test-."
    }
}

Assert-BuildMode

[xml]$buildProperties = Read-Utf8Text (Join-Path $root "Directory.Build.props")
$sharedProperties = @($buildProperties.Project.PropertyGroup)[0]
$canonicalVersion = [string]$sharedProperties.Version
$canonicalReleaseSequence = [int]$sharedProperties.InstallerReleaseSequence
if ([string]::IsNullOrWhiteSpace($canonicalVersion) -or $canonicalReleaseSequence -lt 1) {
    throw "Directory.Build.props debe definir Version e InstallerReleaseSequence positivo."
}
if ($canonicalVersion -cnotmatch '^[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*$') {
    throw "Directory.Build.props contiene una versión no apta para un nombre de archivo portable."
}
$canonicalArchiveName = "Trazio-Asistente-Reunion-v$canonicalVersion-win-x64.zip"

if (-not $AllowTestOverrides) {
    $PayloadDirectory = $canonicalPayloadDirectory
    $PayloadManifestPath = $canonicalPayloadManifestPath
    $OutputDirectory = $artifacts
    $ArchiveName = $canonicalArchiveName
}

$PayloadDirectory = [System.IO.Path]::GetFullPath($PayloadDirectory)
$PayloadManifestPath = [System.IO.Path]::GetFullPath($PayloadManifestPath)
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
if (-not (Test-Path -LiteralPath $PayloadDirectory -PathType Container)) {
    throw "No existe el payload que se debe empaquetar: $PayloadDirectory"
}
if (-not (Test-Path -LiteralPath $PayloadManifestPath -PathType Leaf)) {
    throw "No existe el manifiesto verificado del payload: $PayloadManifestPath"
}
Assert-NoReparsePoint -Path $PayloadManifestPath -Description "El manifiesto del payload"

if (-not $AllowTestOverrides) {
    if ($PayloadDirectory -cne $canonicalPayloadDirectory -or
        $PayloadManifestPath -cne $canonicalPayloadManifestPath -or
        $OutputDirectory -cne $artifacts -or
        $ArchiveName -cne $canonicalArchiveName) {
        throw "Las rutas o el nombre productivos no son canónicos."
    }
    Assert-AncestorsAreNotReparsePoints -Path $PayloadDirectory -StopAt $artifacts
    Assert-AncestorsAreNotReparsePoints -Path ([System.IO.Path]::GetDirectoryName($PayloadManifestPath)) -StopAt $artifacts
}
else {
    Assert-AncestorsAreNotReparsePoints -Path $PayloadDirectory -StopAt ([System.IO.Path]::GetFullPath($TestWorkspaceRoot))
    Assert-AncestorsAreNotReparsePoints -Path ([System.IO.Path]::GetDirectoryName($PayloadManifestPath)) -StopAt ([System.IO.Path]::GetFullPath($TestWorkspaceRoot))
}

$manifest = Read-PayloadManifest $PayloadManifestPath
if (-not $AllowTestOverrides -and
    ($manifest.Version -cne $canonicalVersion -or $manifest.ReleaseSequence -ne $canonicalReleaseSequence)) {
    throw "La versión o secuencia del manifiesto no coincide con Directory.Build.props."
}
Assert-PayloadMatchesManifest -PayloadRoot $PayloadDirectory -Manifest $manifest

$allowedOutputRoot = if ($AllowTestOverrides) { [System.IO.Path]::GetFullPath($TestWorkspaceRoot) } else { $artifacts }
$existingOutputAncestor = [System.IO.DirectoryInfo]::new($OutputDirectory)
while ($null -ne $existingOutputAncestor -and -not $existingOutputAncestor.Exists) {
    $existingOutputAncestor = $existingOutputAncestor.Parent
}
if ($null -eq $existingOutputAncestor -or -not (Test-PathWithinRoot -Candidate $existingOutputAncestor.FullName -AllowedRoot $allowedOutputRoot)) {
    throw "La carpeta de salida no desciende de la raíz permitida."
}
Assert-AncestorsAreNotReparsePoints -Path $existingOutputAncestor.FullName -StopAt $allowedOutputRoot
if (-not (Test-Path -LiteralPath $OutputDirectory -PathType Container)) {
    [System.IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
}
Assert-AncestorsAreNotReparsePoints -Path $OutputDirectory -StopAt $allowedOutputRoot

$archivePath = Join-Path $OutputDirectory $ArchiveName
$checksumPath = "$archivePath.sha256"
$transactionId = [Guid]::NewGuid().ToString("N")
$temporaryArchivePath = Join-Path $OutputDirectory ".$ArchiveName.$transactionId.tmp"
$temporaryChecksumPath = Join-Path $OutputDirectory ".$ArchiveName.sha256.$transactionId.tmp"
$archiveBackupPath = Join-Path $OutputDirectory ".$ArchiveName.$transactionId.backup"
$checksumBackupPath = Join-Path $OutputDirectory ".$ArchiveName.sha256.$transactionId.backup"
$archiveExisted = Test-Path -LiteralPath $archivePath -PathType Leaf
$checksumExisted = Test-Path -LiteralPath $checksumPath -PathType Leaf
if ($archiveExisted) {
    Assert-NoReparsePoint -Path $archivePath -Description "El ZIP de salida existente"
}
if ($checksumExisted) {
    Assert-NoReparsePoint -Path $checksumPath -Description "El sidecar existente"
}
$archiveInstalled = $false
$checksumInstalled = $false
$completed = $false
$rollbackCompleted = $false
$preserveBackups = $false

try {
    New-DeterministicZip -PayloadRoot $PayloadDirectory -Manifest $manifest -DestinationPath $temporaryArchivePath
    Assert-ZipMatchesManifest -ZipPath $temporaryArchivePath -Manifest $manifest
    $archiveHash = Get-FileSha256 $temporaryArchivePath
    $checksumText = "$archiveHash  $ArchiveName`n"
    [System.IO.File]::WriteAllText($temporaryChecksumPath, $checksumText, [System.Text.UTF8Encoding]::new($false))
    if ([System.IO.File]::ReadAllText($temporaryChecksumPath, [System.Text.UTF8Encoding]::new($false, $true)) -cne $checksumText) {
        throw "El sidecar SHA-256 temporal no se pudo verificar."
    }
    if ($InjectTestFailure -ceq "BeforeCommit") {
        throw "Fallo sintético antes del reemplazo atómico."
    }

    Install-Artifact -Temporary $temporaryArchivePath -Destination $archivePath -Backup $archiveBackupPath -Existed $archiveExisted
    $archiveInstalled = $true
    if ($InjectTestFailure -in @("AfterArchiveReplace", "DuringRollback")) {
        throw "Fallo sintético después de reemplazar el ZIP."
    }
    Install-Artifact -Temporary $temporaryChecksumPath -Destination $checksumPath -Backup $checksumBackupPath -Existed $checksumExisted
    $checksumInstalled = $true

    Assert-ZipMatchesManifest -ZipPath $archivePath -Manifest $manifest
    $installedHash = Get-FileSha256 $archivePath
    $installedChecksum = [System.IO.File]::ReadAllText($checksumPath, [System.Text.UTF8Encoding]::new($false, $true))
    if ($installedHash -cne $archiveHash -or $installedChecksum -cne $checksumText) {
        throw "El ZIP o su sidecar final no coincide con los temporales verificados."
    }
    $completed = $true
}
catch {
    $failure = $_
    try {
        if ($checksumInstalled -or $checksumExisted) {
            Restore-Artifact -Destination $checksumPath -Backup $checksumBackupPath -Existed $checksumExisted
        }
        if ($InjectTestFailure -ceq "DuringRollback") {
            throw "Fallo sintético durante el rollback."
        }
        if ($archiveInstalled -or $archiveExisted) {
            Restore-Artifact -Destination $archivePath -Backup $archiveBackupPath -Existed $archiveExisted
        }
        $rollbackCompleted = $true
    }
    catch {
        $preserveBackups = $true
        $availableBackups = @(@($archiveBackupPath, $checksumBackupPath) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
        $backupDescription = if ($availableBackups.Count -gt 0) { $availableBackups -join ', ' } else { "ninguna copia llegó a materializarse" }
        throw "Falló el empaquetado y también el rollback de artefactos: $($failure.Exception.Message) Rollback: $($_.Exception.Message) Copias de respaldo preservadas: $backupDescription"
    }
    throw $failure
}
finally {
    foreach ($temporaryPath in @($temporaryArchivePath, $temporaryChecksumPath)) {
        if (Test-Path -LiteralPath $temporaryPath -PathType Leaf) {
            [System.IO.File]::Delete($temporaryPath)
        }
    }
    if (-not $preserveBackups -and ($completed -or $rollbackCompleted)) {
        foreach ($backupPath in @($archiveBackupPath, $checksumBackupPath)) {
            if (Test-Path -LiteralPath $backupPath -PathType Leaf) {
                [System.IO.File]::Delete($backupPath)
            }
        }
    }
}

if (-not $completed) {
    throw "El empaquetado no completó su transacción."
}

Write-Host "ZIP portable verificado: $archivePath"
Write-Host "SHA-256: $archiveHash"
Write-Host "Entradas: $($manifest.Files.Count); versión: $($manifest.Version); secuencia: $($manifest.ReleaseSequence)"
