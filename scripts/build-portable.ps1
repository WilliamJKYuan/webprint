param(
    [switch]$IncludeData,
    [string]$DataDirectory,
    [string]$LibreOfficeDirectory,
    [string]$SumatraDirectory,
    [string]$NugetSource
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$packageId = 'WebPrint-win-x64-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 6)
$stage = Join-Path $projectRoot "artifacts\$packageId"
$dist = Join-Path $projectRoot 'dist'
$zipFile = Join-Path $dist "$packageId.zip"
New-Item -ItemType Directory -Path $stage, $dist -Force | Out-Null
Push-Location -LiteralPath $projectRoot
$dataLock = $null
try {
    if ($IncludeData) {
        if (-not $DataDirectory) {
            $sourceSettings = Get-Content -LiteralPath 'appsettings.json' -Raw | ConvertFrom-Json
            $configuredData = $sourceSettings.Storage.Path
            if (-not $configuredData) { $configuredData = 'data' }
            $DataDirectory = if ([IO.Path]::IsPathRooted($configuredData)) { [IO.Path]::GetFullPath($configuredData) } else { [IO.Path]::GetFullPath((Join-Path $projectRoot $configuredData)) }
        }
        $DataDirectory = (Resolve-Path -LiteralPath $DataDirectory).Path
        if (-not (Test-Path -LiteralPath (Join-Path $DataDirectory 'webprint.db'))) { throw 'Source database does not exist.' }
        try { $dataLock = [IO.File]::Open((Join-Path $DataDirectory 'server.lock'), 'OpenOrCreate', 'ReadWrite', 'None') }
        catch { throw 'Stop the source WebPrint server before packaging its data.' }
    }
    $publishArgs = @('publish', 'WebPrint.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=false', '-o', $stage)
    if ($NugetSource) { $publishArgs += @('--source', $NugetSource) }
    dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { throw 'Self-contained publish failed.' }
    $desktopStage = Join-Path $projectRoot "artifacts\desktop-$packageId"
    $desktopArgs = @('publish', 'Launcher/WebPrint.Desktop.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=false', '-o', $desktopStage)
    if ($NugetSource) { $desktopArgs += @('--source', $NugetSource) }
    dotnet @desktopArgs
    if ($LASTEXITCODE -ne 0) { throw 'Desktop launcher publish failed.' }
    Get-ChildItem -LiteralPath $desktopStage -Force | Copy-Item -Destination $stage -Recurse -Force
    # Ship clean portable settings; never package passwords or machine-specific absolute paths.
    $settings = Get-Content -LiteralPath (Join-Path $stage 'appsettings.json') -Raw | ConvertFrom-Json
    $settings.Storage.Path = 'data'
    $settings.Printing.Enabled = $false
    $settings.Printing.OfficeConverter = 'libreoffice'
    $settings.Printing.SumatraPath = ''
    $settings.Printing.LibreOfficePath = ''
    $settings | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $stage 'appsettings.json') -Encoding utf8
    foreach ($file in @('Start-WebPrint.cmd', 'Start-LocalOnly.cmd', 'Start-Console.ps1')) { Copy-Item -LiteralPath (Join-Path $projectRoot "portable\$file") -Destination $stage }
    Copy-Item -LiteralPath (Join-Path $projectRoot 'MIGRATION.md') -Destination $stage
    Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination $stage
    Copy-Item -LiteralPath (Join-Path $projectRoot 'THIRD-PARTY.md') -Destination $stage
    Copy-Item -LiteralPath (Join-Path $projectRoot 'docs') -Destination $stage -Recurse -Force
    if (-not $LibreOfficeDirectory) { $LibreOfficeDirectory = Join-Path $projectRoot 'tools\LibreOffice' }
    if (-not $SumatraDirectory) { $SumatraDirectory = Join-Path $projectRoot 'tools\SumatraPDF' }
    foreach ($entry in @(@($LibreOfficeDirectory, 'LibreOffice'), @($SumatraDirectory, 'SumatraPDF'))) {
        if (Test-Path -LiteralPath $entry[0]) {
            $destination = Join-Path $stage ('tools\' + $entry[1])
            New-Item -ItemType Directory -Path $destination -Force | Out-Null
            Get-ChildItem -LiteralPath $entry[0] -Force | Copy-Item -Destination $destination -Recurse -Force
        }
    }
    if ($IncludeData) {
        $destination = Join-Path $stage 'data'
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        Get-ChildItem -LiteralPath $DataDirectory -Force | Where-Object Name -ne 'server.lock' | Copy-Item -Destination $destination -Recurse -Force
    }
    # Native apphosts point at dll/<entry>.dll, keeping runtime DLLs out of the app root.
    $runtimeConfig = Get-Content -LiteralPath (Join-Path $stage 'WebPrint.runtimeconfig.json') -Raw | ConvertFrom-Json
    $runtimeVersion = ($runtimeConfig.runtimeOptions.includedFrameworks | Where-Object name -eq 'Microsoft.NETCore.App' | Select-Object -First 1).version
    $dotnetRoot = Split-Path -Parent (Get-Command dotnet).Source
    $hostTemplate = Join-Path $dotnetRoot "packs\Microsoft.NETCore.App.Host.win-x64\$runtimeVersion\runtimes\win-x64\native\apphost.exe"
    if (-not (Test-Path -LiteralPath $hostTemplate)) {
        $packageRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
        $hostTemplate = Join-Path $packageRoot "microsoft.netcore.app.host.win-x64\$runtimeVersion\runtimes\win-x64\native\apphost.exe"
    }
    if (-not (Test-Path -LiteralPath $hostTemplate)) { throw 'The matching win-x64 apphost template is missing.' }
    $organizerArgs = @('build', 'BuildTools/OrganizePublish/OrganizePublish.csproj', '-c', 'Release')
    if ($NugetSource) { $organizerArgs += @('--source', $NugetSource) }
    dotnet @organizerArgs
    if ($LASTEXITCODE -ne 0) { throw 'Publish organizer build failed.' }
    dotnet BuildTools/OrganizePublish/bin/Release/net9.0/OrganizePublish.dll $stage $hostTemplate
    if ($LASTEXITCODE -ne 0) { throw 'Publish organization failed.' }
    $manifest = [ordered]@{
        package = $packageId; runtime = 'win-x64'; selfContained = $true
        desktop = $true; configurablePort = $true
        runtimeFolder = 'dll'; languageFolder = 'lang'; tray = $true; desktopPrintingSwitch = $true
        officeConversionProviders = @('libreoffice', 'microsoft-office')
        includesData = [bool]$IncludeData
        sumatra = (Test-Path -LiteralPath (Join-Path $stage 'tools\SumatraPDF\SumatraPDF.exe'))
        libreOffice = (Test-Path -LiteralPath (Join-Path $stage 'tools\LibreOffice\program\soffice.exe'))
        createdUtc = [DateTime]::UtcNow.ToString('O')
    }
    $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $stage 'package-info.json') -Encoding utf8
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $zipFile, [IO.Compression.CompressionLevel]::Optimal, $false)
    (Get-FileHash -LiteralPath $zipFile -Algorithm SHA256).Hash | Set-Content -LiteralPath "$zipFile.sha256" -Encoding ascii
    Write-Output "Package: $zipFile"
    Write-Output "Unpacked: $stage"
    Write-Output ($manifest | ConvertTo-Json -Compress)
} finally {
    if ($dataLock) { $dataLock.Dispose() }
    Pop-Location
}
