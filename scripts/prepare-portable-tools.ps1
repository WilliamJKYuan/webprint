# Extracts official tools into this project; does not register an Office installation.
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$toolsRoot = Join-Path $projectRoot 'tools'
$downloads = Join-Path $toolsRoot 'downloads'
New-Item -ItemType Directory -Path $downloads -Force | Out-Null
function Download-File([string]$Url, [string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) {
        curl.exe -fL --retry 2 --connect-timeout 15 --max-time 300 -o $Path $Url
        if ($LASTEXITCODE -ne 0) { throw "Download failed: $Url" }
    }
}
$sumatraZip = Join-Path $downloads 'SumatraPDF.zip'
Download-File 'https://www.sumatrapdfreader.org/dl/rel/3.6.1/SumatraPDF-3.6.1-64.zip' $sumatraZip
$sumatraRoot = Join-Path $toolsRoot 'SumatraPDF'
Expand-Archive -LiteralPath $sumatraZip -DestinationPath $sumatraRoot -Force
$upstreamExe = Join-Path $sumatraRoot 'SumatraPDF-3.6.1-64.exe'
if ((Get-AuthenticodeSignature -LiteralPath $upstreamExe).Status -ne 'Valid') { throw 'SumatraPDF signature verification failed.' }
Copy-Item -LiteralPath $upstreamExe -Destination (Join-Path $sumatraRoot 'SumatraPDF.exe') -Force
$msi = Join-Path $downloads 'LibreOffice.msi'
Download-File 'https://download.documentfoundation.org/libreoffice/stable/26.8.1/win/x86_64/LibreOffice_26.8.1_Win_x86-64.msi' $msi
if ((Get-FileHash -LiteralPath $msi -Algorithm SHA256).Hash.ToLowerInvariant() -ne 'c6298b10cfa1748bcbedad94c62ce771e240e5021bfd6d7a967cd571b04b5312') { throw 'LibreOffice SHA-256 verification failed.' }
$extractRoot = Join-Path $toolsRoot 'LibreOffice-extract'
$log = Join-Path $downloads 'libreoffice-extract.log'
$extractArgs = @('/a', "`"$msi`"", '/qn', '/norestart', "TARGETDIR=`"$extractRoot`"", '/L*v', "`"$log`"")
$process = Start-Process -FilePath 'msiexec.exe' -ArgumentList $extractArgs -WindowStyle Hidden -Wait -PassThru
if ($process.ExitCode -notin @(0, 3010)) { throw "LibreOffice extraction failed ($($process.ExitCode)); see $log" }
$officeExe = Get-ChildItem -LiteralPath $extractRoot -Recurse -Filter 'soffice.exe' | Select-Object -First 1
if (-not $officeExe) { throw 'LibreOffice executable missing from extracted distribution.' }
$officeRoot = $officeExe.Directory.Parent.FullName
$portableRoot = Join-Path $toolsRoot 'LibreOffice'
New-Item -ItemType Directory -Path $portableRoot -Force | Out-Null
Get-ChildItem -LiteralPath $officeRoot -Force | Copy-Item -Destination $portableRoot -Recurse -Force
Write-Output "Portable tools prepared in $toolsRoot"
