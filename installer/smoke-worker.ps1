param([Parameter(Mandatory = $true)][string]$WorkerPath)

$ErrorActionPreference = "Stop"
$pipeName = "trazio-package-smoke-$PID-$([Guid]::NewGuid().ToString('N'))"
$process = Start-Process -FilePath $WorkerPath -ArgumentList @("--pipe", $pipeName) -PassThru -WindowStyle Hidden
$pipe = [System.IO.Pipes.NamedPipeClientStream]::new(".", $pipeName, [System.IO.Pipes.PipeDirection]::InOut, [System.IO.Pipes.PipeOptions]::None)

function Read-Exact([System.IO.Stream]$Stream, [int]$Length) {
    $buffer = [byte[]]::new($Length)
    $offset = 0
    while ($offset -lt $Length) {
        $read = $Stream.Read($buffer, $offset, $Length - $offset)
        if ($read -eq 0) { throw "Worker pipe closed before the response was complete." }
        $offset += $read
    }
    return $buffer
}

try {
    $pipe.Connect(10000)
    $json = [System.Text.Encoding]::UTF8.GetBytes('{"command":"health","language":"es","sampleRate":16000}')
    $header = [System.BitConverter]::GetBytes([int]$json.Length)
    $pipe.Write($header, 0, $header.Length)
    $pipe.Write($json, 0, $json.Length)
    $pipe.Flush()
    $responseLength = [System.BitConverter]::ToInt32((Read-Exact $pipe 4), 0)
    if ($responseLength -le 0 -or $responseLength -gt 1048576) { throw "Worker returned an invalid smoke response length." }
    $response = [System.Text.Encoding]::UTF8.GetString((Read-Exact $pipe $responseLength)) | ConvertFrom-Json
    if ($response.success -ne $true) { throw "Worker health handshake failed: $($response.error)" }
}
finally {
    $pipe.Dispose()
    try {
        $process.Refresh()
        if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
    }
    catch [System.InvalidOperationException] { }
    catch [System.ArgumentException] { }
    $process.Dispose()
}

Write-Host "Worker named-pipe health handshake passed"
