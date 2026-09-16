<#
.SYNOPSIS
    Builds the complete FORTIQ Windows Distribution package (Service + CLI + Desktop).
    Outputs to target/dist/windows/ and creates a ready-to-use zip bundle.
#>

[CmdletBinding()]
param (
    [string]$Version = "0.1.0",
    [switch]$SkipDesktop
)

$ErrorActionPreference = "Stop"

$RootDir = Split-Path -Parent $PSScriptRoot
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "   FORTIQ Windows Distribution Builder     " -ForegroundColor Cyan
Write-Host "   Version: $Version                       " -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan

# 1. Build Backend Binaries (fortiq-service and fortiq CLI)
Write-Host "`n--> Building fortiq-service and fortiq-cli (Release)..." -ForegroundColor Yellow
$CargoPath = "$env:USERPROFILE\.cargo\bin\cargo.exe"
if (-not (Test-Path $CargoPath)) { $CargoPath = "cargo" }

& $CargoPath build --release -p fortiq-service -p fortiq-cli
if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to build backend binaries"
    exit 1
}

# 2. Build Desktop GUI if not skipped
if (-not $SkipDesktop) {
    Write-Host "`n--> Building FORTIQ Desktop Frontend & Tauri..." -ForegroundColor Yellow
    $DesktopDir = Join-Path $RootDir "apps\fortiq-desktop"
    Push-Location $DesktopDir
    try {
        npm.cmd run build
        if ($LASTEXITCODE -ne 0) {
            Write-Error "Failed to build frontend"
            exit 1
        }
        & $CargoPath build --release --manifest-path src-tauri/Cargo.toml
        if ($LASTEXITCODE -ne 0) {
            Write-Error "Failed to compile Tauri desktop backend"
            exit 1
        }
    } finally {
        Pop-Location
    }
}

# 3. Prepare Distribution Directory
$DistDir = Join-Path $RootDir "target\dist\windows"
if (Test-Path $DistDir) {
    Remove-Item -Recurse -Force $DistDir
}
New-Item -ItemType Directory -Path $DistDir -Force | Out-Null

Write-Host "`n--> Collecting distribution files into $DistDir..." -ForegroundColor Yellow
Copy-Item (Join-Path $RootDir "target\release\fortiq-service.exe") -Destination $DistDir -Force
Copy-Item (Join-Path $RootDir "target\release\fortiq.exe") -Destination $DistDir -Force

$DesktopExe = Join-Path $RootDir "target\release\fortiq-desktop.exe"
if (-not (Test-Path $DesktopExe)) {
    $DesktopExe = Join-Path $RootDir "apps\fortiq-desktop\src-tauri\target\release\fortiq-desktop.exe"
}
if (Test-Path $DesktopExe) {
    Copy-Item $DesktopExe -Destination $DistDir -Force
    Write-Host "  [OK] Included fortiq-desktop.exe" -ForegroundColor Green
}

Copy-Item (Join-Path $RootDir "packaging\windows\install.ps1") -Destination $DistDir -Force
Copy-Item (Join-Path $RootDir "packaging\windows\uninstall.ps1") -Destination $DistDir -Force
Copy-Item (Join-Path $RootDir "packaging\windows\fortiq.toml.example") -Destination $DistDir -Force

# 4. Create Zip Archive
$ZipPath = Join-Path $RootDir "target\dist\FORTIQ-$Version-Windows-x64.zip"
Write-Host "`n--> Creating standalone ZIP bundle: $ZipPath..." -ForegroundColor Yellow
if (Test-Path $ZipPath) { Remove-Item -Force $ZipPath }
Compress-Archive -Path "$DistDir\*" -DestinationPath $ZipPath -Force

Write-Host "`n==========================================" -ForegroundColor Green
Write-Host "  Build Completed Successfully!            " -ForegroundColor Green
Write-Host "  Dist Directory: $DistDir                 " -ForegroundColor Green
Write-Host "  Zip Bundle:     $ZipPath                 " -ForegroundColor Green
Write-Host "==========================================" -ForegroundColor Green
