<# .SYNOPSIS Uninstalls FORTIQ while preserving identity and configuration by default. #>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = "High")]
param ([switch]$RemoveData)

$ErrorActionPreference = "Stop"
$InstallDir = "C:\Program Files\FORTIQ"
$DataDir = "C:\ProgramData\FORTIQ"
$RunKeyPath = "Software\Microsoft\Windows\CurrentVersion\Run"
$RunValueName = "FORTIQ Desktop"

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "FORTIQ uninstallation requires Administrator privileges."
}

Get-Process -Name "fortiq-desktop", "FORTIQ" -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue
if (Get-Service -Name "FortiqService" -ErrorAction SilentlyContinue) {
    Stop-Service -Name "FortiqService" -Force -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath (Join-Path $InstallDir "fortiq-service.exe")) {
        & (Join-Path $InstallDir "fortiq-service.exe") service uninstall | Out-Null
    }
}

foreach ($registryView in @([Microsoft.Win32.RegistryView]::Registry64, [Microsoft.Win32.RegistryView]::Registry32)) {
    $registryBase = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
        [Microsoft.Win32.RegistryHive]::LocalMachine,
        $registryView
    )
    $runKey = $registryBase.OpenSubKey($RunKeyPath, $true)
    if ($runKey) {
        try { $runKey.DeleteValue($RunValueName, $false) } finally { $runKey.Dispose() }
    }
    $registryBase.Dispose()
}
Remove-Item -LiteralPath (Join-Path $env:ProgramData "Microsoft\Windows\Start Menu\Programs\FORTIQ") -Recurse -Force -ErrorAction SilentlyContinue
$machinePath = [Environment]::GetEnvironmentVariable("Path", [EnvironmentVariableTarget]::Machine)
$newPath = @($machinePath -split ';' | Where-Object { $_ -and $_ -ne $InstallDir }) -join ';'
[Environment]::SetEnvironmentVariable("Path", $newPath, [EnvironmentVariableTarget]::Machine)
Remove-Item -LiteralPath $InstallDir -Recurse -Force -ErrorAction SilentlyContinue

if ($RemoveData) {
    if ($PSCmdlet.ShouldProcess($DataDir, "Permanently remove FORTIQ identity, configuration, and ticket data")) {
        Remove-Item -LiteralPath $DataDir -Recurse -Force
    }
} else {
    Write-Host "Preserved identity and configuration in $DataDir." -ForegroundColor Yellow
    Write-Host "Use -RemoveData and confirm explicitly to erase them."
}
Write-Host "FORTIQ has been uninstalled." -ForegroundColor Green
