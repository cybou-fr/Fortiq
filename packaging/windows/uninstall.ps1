[CmdletBinding()]
param([switch]$RemoveData)

$ErrorActionPreference = "Stop"
$InstallDir = "C:\Program Files\FORTIQ"
$DataDir = "C:\ProgramData\FORTIQ"
$RunKey = "Software\Microsoft\Windows\CurrentVersion\Run"

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "FORTIQ uninstallation requires Administrator privileges."
}

Get-Process -Name fortiq-desktop,fortiq-service,fortiq -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue
if (Test-Path (Join-Path $InstallDir "fortiq-service.exe")) {
    & (Join-Path $InstallDir "fortiq-service.exe") service stop 2>$null
    & (Join-Path $InstallDir "fortiq-service.exe") service uninstall 2>$null
}

$registry = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)
$key = $registry.OpenSubKey($RunKey, $true)
if ($null -ne $key) { $key.DeleteValue("FORTIQ Desktop", $false); $key.Dispose() }
$registry.Dispose()

Remove-Item (Join-Path $env:ProgramData "Microsoft\Windows\Start Menu\Programs\FORTIQ") -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $InstallDir -Recurse -Force -ErrorAction SilentlyContinue
if ($RemoveData) { Remove-Item $DataDir -Recurse -Force -ErrorAction SilentlyContinue }
else { Write-Host "Preserved identity and configuration in $DataDir." -ForegroundColor Yellow }
