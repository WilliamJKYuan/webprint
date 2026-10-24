# Downloads official dependencies. LibreOffice installation requires approval.
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$toolsRoot = Join-Path $projectRoot 'tools'
New-Item -ItemType Directory -Path $toolsRoot -Force | Out-Null
$ProgressPreference = 'SilentlyContinue'
$sumatraRoot = Join-Path $toolsRoot 'SumatraPDF'
$sumatraExe = Join-Path $sumatraRoot 'SumatraPDF-3.6.1-64.exe'
if (-not (Test-Path -LiteralPath $sumatraExe)) {
    $sumatraZip = Join-Path $toolsRoot 'SumatraPDF-3.6.1-64.zip'
    Invoke-WebRequest -Uri 'https://www.sumatrapdfreader.org/dl/rel/3.6.1/SumatraPDF-3.6.1-64.zip' -OutFile $sumatraZip
    Expand-Archive -LiteralPath $sumatraZip -DestinationPath $sumatraRoot -Force
}
if (-not (Test-Path -LiteralPath $sumatraExe)) { throw 'SumatraPDF executable was not found after extraction.' }
$signature = Get-AuthenticodeSignature -LiteralPath $sumatraExe
if ($signature.Status -ne 'Valid') { throw "SumatraPDF signature verification failed: $($signature.Status)" }
$libreOfficeExe = 'C:\Program Files\LibreOffice\program\soffice.exe'
if (-not (Test-Path -LiteralPath $libreOfficeExe)) {
    $installer = Join-Path $toolsRoot 'LibreOffice_26.8.1_Win_x86-64.msi'
    $expectedHash = 'c6298b10cfa1748bcbedad94c62ce771e240e5021bfd6d7a967cd571b04b5312'
    if (-not (Test-Path -LiteralPath $installer)) {
        Invoke-WebRequest -Uri 'https://download.documentfoundation.org/libreoffice/stable/26.8.1/win/x86_64/LibreOffice_26.8.1_Win_x86-64.msi' -OutFile $installer
    }
    if ((Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expectedHash) { throw 'LibreOffice SHA-256 does not match the official value.' }
    $installLog = Join-Path $toolsRoot 'libreoffice-install.log'
    $installArgs = @('/i', "`"$installer`"", '/qn', '/norestart', 'REGISTER_ALL_MSO_TYPES=0', '/L*v', "`"$installLog`"")
    $installProcess = Start-Process -FilePath 'msiexec.exe' -ArgumentList $installArgs -WindowStyle Hidden -Wait -PassThru
    if ($installProcess.ExitCode -notin @(0, 3010)) { throw "LibreOffice installation failed ($($installProcess.ExitCode)). See $installLog" }
}
if (-not (Test-Path -LiteralPath $libreOfficeExe)) { throw 'LibreOffice executable was not found after installation.' }
$configFile = Join-Path $projectRoot 'appsettings.json'
$settings = Get-Content -LiteralPath $configFile -Raw | ConvertFrom-Json
$settings.Printing.SumatraPath = $sumatraExe
$settings.Printing.LibreOfficePath = $libreOfficeExe
$settings | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $configFile -Encoding utf8
Write-Output 'Dependencies configured. The physical printing switch was not changed.'
