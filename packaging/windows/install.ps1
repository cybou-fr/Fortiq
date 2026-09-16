<#
.SYNOPSIS
    Installs FORTIQ Sovereign Remote Administration on Windows.
    Deploys fortiq-service, fortiq (CLI), and fortiq-desktop (GUI),
    registers the Windows Service, and sets up configuration.
#>

[CmdletBinding()]
param (
    [switch]$Force
)

$ErrorActionPreference = "Stop"

# Ensure Administrator Privileges
$CurrentPrincipal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $CurrentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "Elevating privileges to Administrator..." -ForegroundColor Yellow
    Start-Process powershell.exe -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`"" -Verb RunAs
    exit
}

Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "   FORTIQ Windows Production Installer     " -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$InstallDir = "C:\Program Files\FORTIQ"
$DataDir = "C:\ProgramData\FORTIQ"

# 1. Stop existing service if running
Write-Host "--> Checking existing services..." -ForegroundColor Cyan
if (Get-Service -Name "FortiqService" -ErrorAction SilentlyContinue) {
    Write-Host "Stopping and removing existing FortiqService..." -ForegroundColor Yellow
    Stop-Service -Name "FortiqService" -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
    if (Test-Path "$InstallDir\fortiq-service.exe") {
        & "$InstallDir\fortiq-service.exe" service uninstall | Out-Null
    }
}

# Stop any running desktop client
Get-Process -Name "fortiq-desktop", "FORTIQ" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

# 2. Create Target Directories
Write-Host "--> Creating target directories..." -ForegroundColor Cyan
New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
New-Item -ItemType Directory -Path $DataDir -Force | Out-Null

# 3. Copy Binaries
Write-Host "--> Deploying binaries to $InstallDir..." -ForegroundColor Cyan

$Binaries = @("fortiq-service.exe", "fortiq.exe", "fortiq-desktop.exe")
foreach ($bin in $Binaries) {
    $src = Join-Path $ScriptDir $bin
    if (-not (Test-Path $src)) {
        # Check target/release or current directory
        $fallback = Join-Path (Join-Path $ScriptDir "..\..\target\release") $bin
        if (Test-Path $fallback) {
            $src = $fallback
        }
    }

    if (Test-Path $src) {
        Copy-Item -Path $src -Destination "$InstallDir\$bin" -Force
        Write-Host "  [OK] Installed $bin" -ForegroundColor Green
    } elseif ($bin -eq "fortiq-desktop.exe") {
        Write-Host "  [INFO] $bin not found in package; skipping desktop GUI" -ForegroundColor Gray
    } else {
        Write-Error "Required binary $bin not found in $ScriptDir"
        exit 1
    }
}

# 4. Initialize Configuration
Write-Host "--> Initializing configuration in $DataDir..." -ForegroundColor Cyan
$ConfigFile = "$DataDir\fortiq.toml"
if (-not (Test-Path $ConfigFile)) {
    $Template = Join-Path $ScriptDir "fortiq.toml.example"
    if (Test-Path $Template) {
        Copy-Item -Path $Template -Destination $ConfigFile -Force
    } else {
        @"
# FORTIQ Node Configuration
[node]
name = "$env:COMPUTERNAME"

[identity]
path = "C:/ProgramData/FORTIQ/identity.key"

[network]
listen_quic = "0.0.0.0:4001"
relay_peer = "/ip4/51.255.46.58/udp/4001/quic-v1/p2p/12D3KooWRFrWVx2CANXcjXvTNaqkh6APgAecsW94CLwNEg5wsqLy"

[ticket]
path = "C:/ProgramData/FORTIQ/ticket.json"
"@ | Set-Content -Path $ConfigFile -Encoding utf8
    }
    Write-Host "  [OK] Created default configuration: $ConfigFile" -ForegroundColor Green
} else {
    Write-Host "  [INFO] Preserving existing configuration: $ConfigFile" -ForegroundColor Yellow
}

# 5. Add to System PATH
Write-Host "--> Updating System Environment PATH..." -ForegroundColor Cyan
$MachinePath = [Environment]::GetEnvironmentVariable("Path", [EnvironmentVariableTarget]::Machine)
if ($MachinePath -notmatch [regex]::Escape($InstallDir)) {
    $NewPath = "$MachinePath;$InstallDir"
    [Environment]::SetEnvironmentVariable("Path", $NewPath, [EnvironmentVariableTarget]::Machine)
    $env:Path = "$env:Path;$InstallDir"
    Write-Host "  [OK] Added $InstallDir to system PATH" -ForegroundColor Green
} else {
    Write-Host "  [INFO] $InstallDir already present in PATH" -ForegroundColor Gray
}

# 6. Install and Start FortiqService
Write-Host "--> Registering FortiqService..." -ForegroundColor Cyan
& "$InstallDir\fortiq-service.exe" service install --config $ConfigFile
Start-Sleep -Seconds 1

Write-Host "--> Starting FortiqService..." -ForegroundColor Cyan
& "$InstallDir\fortiq-service.exe" service start
Start-Sleep -Seconds 2

# 7. Create Start Menu Shortcuts
$StartMenuDir = "$env:ProgramData\Microsoft\Windows\Start Menu\Programs\FORTIQ"
New-Item -ItemType Directory -Path $StartMenuDir -Force | Out-Null
$WshShell = New-Object -ComObject WScript.Shell

if (Test-Path "$InstallDir\fortiq-desktop.exe") {
    $Shortcut = $WshShell.CreateShortcut("$StartMenuDir\FORTIQ Desktop.lnk")
    $Shortcut.TargetPath = "$InstallDir\fortiq-desktop.exe"
    $Shortcut.WorkingDirectory = $InstallDir
    $Shortcut.Description = "FORTIQ Sovereign Remote Administration Desktop"
    $Shortcut.Save()
    Write-Host "  [OK] Created Start Menu shortcut for Desktop" -ForegroundColor Green

    # Launch Desktop Client in user session
    Start-Process -FilePath "$InstallDir\fortiq-desktop.exe"
    Write-Host "  [OK] Launched FORTIQ Desktop (Tray active)" -ForegroundColor Green
}

$CliShortcut = $WshShell.CreateShortcut("$StartMenuDir\FORTIQ Shell.lnk")
$CliShortcut.TargetPath = "powershell.exe"
$CliShortcut.Arguments = "-NoExit -Command `"Write-Host 'Type fortiq --help to get started' -ForegroundColor Cyan; fortiq status`""
$CliShortcut.Description = "FORTIQ Command Line"
$CliShortcut.Save()

Write-Host "`n==========================================" -ForegroundColor Green
Write-Host "  FORTIQ Installation Complete!           " -ForegroundColor Green
Write-Host "==========================================" -ForegroundColor Green
Write-Host "Service:   FortiqService (RUNNING)"
Write-Host "CLI:       fortiq status"
Write-Host "Config:    $ConfigFile"
Write-Host "Binaries:  $InstallDir"
Write-Host "==========================================" -ForegroundColor Green

# Verify Status
& "$InstallDir\fortiq.exe" status
