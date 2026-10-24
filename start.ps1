param([ValidateRange(1, 65535)][int]$Port = 5080, [switch]$Lan)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$bindAddress = if ($Lan) { '0.0.0.0' } else { '127.0.0.1' }
dotnet run --no-launch-profile --urls "http://${bindAddress}:$Port"
