[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$InstallDir = "C:\Program Files\FORTIQ"
$DataDir = "C:\ProgramData\FORTIQ"
$ConfigFile = Join-Path $DataDir "fortiq.toml"
$ServiceName = "FortiqService"
$RunKey = "Software\Microsoft\Windows\CurrentVersion\Run"

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "FORTIQ installation requires Administrator privileges."
}

function Invoke-Native([scriptblock]$Command, [string]$Message) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        & $Command 2>&1 | ForEach-Object { Write-Host $_ }
        if ($LASTEXITCODE -ne 0) { throw "$Message (exit code $LASTEXITCODE)" }
    } finally {
        $ErrorActionPreference = $previous
    }
}

Get-Process -Name fortiq-desktop,fortiq-service,fortiq -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue
if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    if (Test-Path (Join-Path $InstallDir "fortiq-service.exe")) {
        Invoke-Native { & (Join-Path $InstallDir "fortiq-service.exe") service uninstall } "Failed to remove existing service"
    }
}

New-Item -ItemType Directory -Force -Path $InstallDir,$DataDir | Out-Null
Copy-Item (Join-Path $PSScriptRoot "fortiq-service.exe") $InstallDir -Force
Copy-Item (Join-Path $PSScriptRoot "fortiq.exe") $InstallDir -Force
Copy-Item (Join-Path $PSScriptRoot "fortiq-desktop.exe") $InstallDir -Force
Copy-Item (Join-Path $PSScriptRoot "fortiq.toml") $ConfigFile -Force

$acl = Get-Acl $DataDir
$acl.SetAccessRuleProtection($true, $false)
$system = New-Object System.Security.Principal.SecurityIdentifier("S-1-5-18")
$admins = New-Object System.Security.Principal.SecurityIdentifier("S-1-5-32-544")
$inherit = [System.Security.AccessControl.InheritanceFlags]"ContainerInherit, ObjectInherit"
$rule = [System.Security.AccessControl.FileSystemAccessRule]::new($system, "FullControl", $inherit, "None", "Allow")
$acl.AddAccessRule($rule)
$acl.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new($admins, "FullControl", $inherit, "None", "Allow"))
Set-Acl $DataDir $acl

$machinePath = [Environment]::GetEnvironmentVariable("Path", "Machine")
if (-not (($machinePath -split ";") -contains $InstallDir)) {
    [Environment]::SetEnvironmentVariable("Path", (($machinePath.TrimEnd(";") + ";" + $InstallDir)), "Machine")
}

Invoke-Native { & (Join-Path $InstallDir "fortiq-service.exe") service install --config $ConfigFile } "Failed to install Windows service"
Invoke-Native { & (Join-Path $InstallDir "fortiq-service.exe") service start } "Failed to start Windows service"

$registry = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)
$key = $registry.CreateSubKey($RunKey)
try { $key.SetValue("FORTIQ Desktop", ('"{0}"' -f (Join-Path $InstallDir "fortiq-desktop.exe"))) }
finally { $key.Dispose(); $registry.Dispose() }

$startMenu = Join-Path $env:ProgramData "Microsoft\Windows\Start Menu\Programs\FORTIQ"
New-Item -ItemType Directory -Force -Path $startMenu | Out-Null
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut((Join-Path $startMenu "FORTIQ Desktop.lnk"))
$link.TargetPath = Join-Path $InstallDir "fortiq-desktop.exe"
$link.WorkingDirectory = $InstallDir
$link.Save()

Write-Host "FORTIQ Node installation completed." -ForegroundColor Green
