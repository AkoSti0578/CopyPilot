<#
.SYNOPSIS
  Builds CopyPilot: downloads the latest AzCopy v10, runs the tests, publishes the
  self-contained app and creates the MSI installer and a portable ZIP.

.EXAMPLE
  ./build.ps1                      # version from Directory.Build.props
  ./build.ps1 -Version 1.2.0       # explicit version
  ./build.ps1 -SkipTests

  Output: artifacts\CopyPilot-<version>-x64.msi and artifacts\CopyPilot-<version>-x64-portable.zip
  Created by Arnold Kolkman.
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$Configuration = 'Release',
    [switch]$SkipTests,
    [switch]$SkipAzCopyDownload
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = $PSScriptRoot
$runtime = 'win-x64'

if (-not $Version) {
    [xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
    $Version = $props.Project.PropertyGroup.Version
}
Write-Host "==> Building CopyPilot $Version ($runtime)" -ForegroundColor Cyan

$artifacts = Join-Path $root 'artifacts'
$publishDir = Join-Path $artifacts "publish\$runtime"
$azDir = Join-Path $root 'build\azcopy'
$downloadDir = Join-Path $root 'build\download'

# ---------------------------------------------------------------------------
# 1. AzCopy (latest v10 from Microsoft's permanent download link)
# ---------------------------------------------------------------------------
if (-not $SkipAzCopyDownload -or -not (Test-Path (Join-Path $azDir 'azcopy.exe'))) {
    Write-Host '==> Downloading latest AzCopy v10' -ForegroundColor Cyan
    Remove-Item $azDir, $downloadDir -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $azDir, $downloadDir | Out-Null
    $zip = Join-Path $downloadDir 'azcopy.zip'
    Invoke-WebRequest -Uri 'https://aka.ms/downloadazcopy-v10-windows' -OutFile $zip -UseBasicParsing
    Expand-Archive -Path $zip -DestinationPath $downloadDir -Force
    $exe = Get-ChildItem -Path $downloadDir -Recurse -Filter 'azcopy.exe' | Select-Object -First 1
    if (-not $exe) { throw 'azcopy.exe not found in the downloaded archive.' }
    # azcopy.exe plus its license / third-party notices
    Get-ChildItem -Path $exe.DirectoryName -File | Copy-Item -Destination $azDir
}
$azVersion = (& (Join-Path $azDir 'azcopy.exe') --version) -join ' '
Write-Host "    bundled: $azVersion"

# ---------------------------------------------------------------------------
# 2. Tests
# ---------------------------------------------------------------------------
if (-not $SkipTests) {
    Write-Host '==> Running tests' -ForegroundColor Cyan
    dotnet test (Join-Path $root 'tests\CopyPilot.Core.Tests\CopyPilot.Core.Tests.csproj') -c $Configuration
    if ($LASTEXITCODE) { throw 'Tests failed.' }
}

# ---------------------------------------------------------------------------
# 3. Publish (self-contained single file: no .NET install required)
# ---------------------------------------------------------------------------
Write-Host '==> Publishing' -ForegroundColor Cyan
Remove-Item $publishDir -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish (Join-Path $root 'src\CopyPilot\CopyPilot.csproj') `
    -c $Configuration -r $runtime --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -p:Version=$Version -o $publishDir
if ($LASTEXITCODE) { throw 'Publish failed.' }
if (-not (Test-Path (Join-Path $publishDir 'azcopy\azcopy.exe'))) { throw 'azcopy.exe is missing from the publish output.' }

# ---------------------------------------------------------------------------
# 4. MSI (WiX Toolset v5 via NuGet – nothing to install)
# ---------------------------------------------------------------------------
Write-Host '==> Building MSI' -ForegroundColor Cyan
$msiOut = Join-Path $artifacts 'msi'
dotnet build (Join-Path $root 'installer\CopyPilot.Installer.wixproj') -c $Configuration `
    -p:Version=$Version -p:AppPublishDir=$publishDir -o $msiOut
if ($LASTEXITCODE) { throw 'MSI build failed.' }
$msi = Get-ChildItem $msiOut -Filter '*.msi' | Select-Object -First 1
Copy-Item $msi.FullName -Destination $artifacts -Force

# ---------------------------------------------------------------------------
# 5. Portable ZIP
# ---------------------------------------------------------------------------
$portable = Join-Path $artifacts "CopyPilot-$Version-x64-portable.zip"
Remove-Item $portable -Force -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $portable

Write-Host ''
Write-Host "==> Done" -ForegroundColor Green
Write-Host "    MSI:      $(Join-Path $artifacts $msi.Name)"
Write-Host "    Portable: $portable"
