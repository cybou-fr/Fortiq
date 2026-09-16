<#
.SYNOPSIS
    Uninstalls FORTIQ Sovereign Remote Administration from Windows.
#>

[CmdletBinding()]
param (
    [switch]$RemoveData
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
Write-Host "   FORTIQ Windows Uninstaller             " -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan

$InstallDir = "C:\Program Files\FORTIQ"
$DataDir = "C:\ProgramData\FORTIQ"
$StartMenuDir = "$env:ProgramData\Microsoft\Windows\Start Menu\Programs\FORTIQ"

# 1. Stop and remove desktop process
Write-Host "--> Stopping FORTIQ Desktop..." -ForegroundColor Cyan
Get-Process -Name "fortiq-desktop", "FORTIQ" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

# 2. Stop and remove Windows Service
Write-Host "--> Stopping and removing FortiqService..." -ForegroundColor Cyan
if (Get-Service -Name "FortiqService" -ErrorAction SilentlyContinue) {
    Stop-Service -Name "FortiqService" -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
    if (Test-Path "$InstallDir\fortiq-service.exe") {
        & "$InstallDir\fortiq-service.exe" service uninstall | Out-Null
    }
}

# 3. Remove Start Menu shortcuts
Write-Host "--> Removing Start Menu shortcuts..." -ForegroundColor Cyan
if (Test-Path $StartMenuDir) {
    Remove-Item -Recurse -Force $StartMenuDir -ErrorAction SilentlyContinue
}

# 4. Remove from System PATH
Write-Host "--> Removing from System Environment PATH..." -ForegroundColor Cyan
$MachinePath = [Environment]::GetEnvironmentVariable("Path", [EnvironmentVariableTarget]::Machine)
if ($MachinePath -match [regex]::Escape($InstallDir)) {
    $NewPath = ($MachinePath.Split(';') | Where-Object { $_ -ne $InstallDir }) -join ';'
    [Environment]::SetEnvironmentVariable("Path", $NewPath, [EnvironmentVariableTarget]::Machine)
    Write-Host "  [OK] Removed $InstallDir from PATH" -ForegroundColor Green
}

# 5. Remove Program Files Directory
Write-Host "--> Removing application binaries from $InstallDir..." -ForegroundColor Cyan
if (Test-Path $InstallDir) {
    Remove-Item -Recurse -Force $InstallDir -ErrorAction SilentlyContinue
}

# 6. Optional Data Directory Removal
if ($RemoveData) {
    Write-Host "--> Removing configuration and data from $DataDir..." -ForegroundColor Cyan
    Remove-Item -Recurse -Force $DataDir -ErrorAction SilentlyContinue
} else {
    Write-Host "  [INFO] Preserving configuration and identity in $DataDir (use -RemoveData to wipe)" -ForegroundColor Yellow
}

Write-Host "`nFORTIQ has been successfully uninstalled." -ForegroundColor Green
