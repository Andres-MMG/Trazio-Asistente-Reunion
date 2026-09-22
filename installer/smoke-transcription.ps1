param(
    [Parameter(Mandatory = $true)][string]$WorkerPath,
    [Parameter(Mandatory = $true)][string]$ModelPath,
    [Parameter(Mandatory = $true)][string]$AudioPath,
    [string]$Language = 'en',
    [string[]]$ExpectedText = @(),
    [ValidateRange(1, 600)][int]$TimeoutSeconds = 120,
    [string]$OutputPath = 'artifacts/model-validation/transcription-result.json'
)
$ErrorActionPreference = 'Stop'
$WorkerPath = (Resolve-Path -LiteralPath $WorkerPath).Path
$ModelPath = (Resolve-Path -LiteralPath $ModelPath).Path
$AudioPath = (Resolve-Path -LiteralPath $AudioPath).Path
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($OutputPath)) | Out-Null
$result = [ordered]@{ success = $false; timestampUtc = [DateTime]::UtcNow.ToString('o'); workerPath = $WorkerPath; modelPath = $ModelPath; audioPath = $AudioPath; language = $Language; expectedText = $ExpectedText; modelLoadMilliseconds = $null; inferenceMilliseconds = $null; transcript = $null; segments = @(); error = $null }
$process = $null
$pipe = $null
$deadline = [Diagnostics.Stopwatch]::StartNew()
function Await-Operation($Task) {
    $remaining = [int][Math]::Max(1, ($TimeoutSeconds * 1000 - $deadline.ElapsedMilliseconds))
    if ($deadline.ElapsedMilliseconds -ge $TimeoutSeconds * 1000 -or -not $Task.Wait($remaining)) { throw "Worker operation exceeded total timeout of $TimeoutSeconds seconds." }
    return $Task.GetAwaiter().GetResult()
}
function Read-Exact([int]$Length) {
    $buffer = [byte[]]::new($Length)
    $offset = 0
    while ($offset -lt $Length) {
        $read = Await-Operation ($pipe.ReadAsync($buffer, $offset, $Length - $offset))
        if ($read -eq 0) { throw 'Worker pipe closed before response completed.' }
        $offset += $read
    }
    return ,$buffer
}
function Send-Request($Request) {
    $payload = [Text.Encoding]::UTF8.GetBytes(($Request | ConvertTo-Json -Compress -Depth 6))
    if ($payload.Length -gt 33554432) { throw 'IPC request exceeds 32 MiB.' }
    $header = [BitConverter]::GetBytes([int]$payload.Length)
    Await-Operation ($pipe.WriteAsync($header, 0, 4))
    Await-Operation ($pipe.WriteAsync($payload, 0, $payload.Length))
    $length = [BitConverter]::ToInt32((Read-Exact 4), 0)
    if ($length -le 0 -or $length -gt 33554432) { throw 'Invalid IPC response length.' }
    $response = [Text.Encoding]::UTF8.GetString((Read-Exact $length)) | ConvertFrom-Json
    if ($response.success -ne $true) { throw "Worker $($Request.command) failed: $($response.error)" }
    return $response
}
try {
    $bytes = [IO.File]::ReadAllBytes($AudioPath)
    if ($bytes.Length -lt 12 -or [Text.Encoding]::ASCII.GetString($bytes, 0, 4) -ne 'RIFF' -or [Text.Encoding]::ASCII.GetString($bytes, 8, 4) -ne 'WAVE') { throw 'Audio must be a RIFF WAVE file.' }
    $riffEnd = [long][BitConverter]::ToUInt32($bytes, 4) + 8
    if ($riffEnd -gt $bytes.Length -or $riffEnd -lt 12) { throw 'Invalid RIFF size.' }
    $offset = 12L
    $formatFound = $false
    $pcm = $null
    while ($offset + 8 -le $riffEnd) {
        $id = [Text.Encoding]::ASCII.GetString($bytes, [int]$offset, 4)
        $size = [long][BitConverter]::ToUInt32($bytes, [int]($offset + 4))
        $start = $offset + 8
        if ($start + $size -gt $riffEnd) { throw 'Truncated WAV chunk.' }
        if ($id -eq 'fmt ') {
            if ($formatFound -or $size -lt 16) { throw 'Invalid or duplicate WAV format.' }
            if ([BitConverter]::ToUInt16($bytes, $start) -ne 1 -or [BitConverter]::ToUInt16($bytes, $start + 2) -ne 1 -or [BitConverter]::ToUInt32($bytes, $start + 4) -ne 16000 -or [BitConverter]::ToUInt32($bytes, $start + 8) -ne 32000 -or [BitConverter]::ToUInt16($bytes, $start + 12) -ne 2 -or [BitConverter]::ToUInt16($bytes, $start + 14) -ne 16) { throw 'Audio must be PCM16 mono at 16000 Hz.' }
            $formatFound = $true
        }
        if ($id -eq 'data') {
            if ($null -ne $pcm -or $size -le 0 -or $size % 2 -ne 0 -or $size -gt 24000000) { throw 'Invalid, duplicate, or oversized WAV data.' }
            $pcm = [byte[]]::new([int]$size)
            [Array]::Copy($bytes, $start, $pcm, 0L, $size)
        }
        $offset = $start + $size + ($size % 2)
    }
    if ($offset -ne $riffEnd -or -not $formatFound -or $null -eq $pcm) { throw 'Incomplete WAV chunks.' }
    $result.audioDurationSeconds = $pcm.Length / 32000
    $result.audioSha256 = (Get-FileHash -LiteralPath $AudioPath -Algorithm SHA256).Hash
    $result.modelSha256 = (Get-FileHash -LiteralPath $ModelPath -Algorithm SHA256).Hash
    $pipeName = "trazio-transcription-smoke-$PID-$([Guid]::NewGuid().ToString('N'))"
    $process = Start-Process -FilePath $WorkerPath -ArgumentList @('--pipe', $pipeName) -PassThru -WindowStyle Hidden -RedirectStandardOutput "$OutputPath.stdout.log" -RedirectStandardError "$OutputPath.stderr.log"
    $result.workerProcessId = $process.Id
    $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', $pipeName, [IO.Pipes.PipeDirection]::InOut, [IO.Pipes.PipeOptions]::Asynchronous)
    Await-Operation ($pipe.ConnectAsync([Math]::Min(10000, $TimeoutSeconds * 1000)))
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $null = Send-Request @{ command = 'start'; modelPath = $ModelPath; language = $Language }
    $result.modelLoadMilliseconds = $timer.ElapsedMilliseconds
    $timer.Restart()
    $workId = [Guid]::NewGuid().ToString('N')
    $response = Send-Request @{ command = 'transcribe'; workId = $workId; pcm16 = [Convert]::ToBase64String($pcm); sampleRate = 16000; language = $Language }
    $result.inferenceMilliseconds = $timer.ElapsedMilliseconds
    if ($response.workId -ne $workId) { throw 'Worker returned a different work ID.' }
    $result.segments = @($response.segments)
    $result.transcript = ($response.segments | ForEach-Object { $_.text }) -join ' '
    $null = Send-Request @{ command = 'stop' }
    if ([string]::IsNullOrWhiteSpace($result.transcript)) { throw 'Transcription was empty.' }
    foreach ($expected in $ExpectedText) {
        if ($result.transcript.IndexOf($expected, [StringComparison]::OrdinalIgnoreCase) -lt 0) { throw "Transcript did not contain expected text: $expected" }
    }
    $result.success = $true
}
catch { $result.error = $_.Exception.Message }
finally {
    if ($null -ne $pipe) { $pipe.Dispose() }
    if ($null -ne $process) {
        try {
            if (-not $process.WaitForExit(3000)) { $process.Kill(); $process.WaitForExit(3000) | Out-Null }
            $result.workerExitCode = $process.ExitCode
        }
        finally { $process.Dispose() }
    }
    $result.totalMilliseconds = $deadline.ElapsedMilliseconds
    [IO.File]::WriteAllText($OutputPath, ($result | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
}
if (-not $result.success) { throw "Transcription smoke failed. Evidence: $OutputPath. $($result.error)" }
Write-Host "Transcription smoke passed. Evidence: $OutputPath"
