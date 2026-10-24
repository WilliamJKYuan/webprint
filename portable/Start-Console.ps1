param([switch]$LocalOnly)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
try {
    $inputPort = Read-Host 'Web port [5080]'
    if ([string]::IsNullOrWhiteSpace($inputPort)) { $inputPort = '5080' }
    $portNumber = 0
    if (-not [int]::TryParse($inputPort, [ref]$portNumber) -or $portNumber -lt 1 -or $portNumber -gt 65535) { throw 'Port must be an integer from 1 to 65535.' }
    $hostAddress = if ($LocalOnly) { '127.0.0.1' } else { '0.0.0.0' }
    & (Join-Path $PSScriptRoot 'WebPrint.exe') --urls "http://${hostAddress}:$portNumber"
} catch { Write-Host $_.Exception.Message -ForegroundColor Red }
Read-Host 'Press Enter to close'
