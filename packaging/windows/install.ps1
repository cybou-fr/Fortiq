<#
.SYNOPSIS
    Installs the complete FORTIQ Windows product.
.DESCRIPTION
    Shared engine for the role-specific Setup executables. The role is explicit,
    and Client installation is rejected without a valid Operator PeerId.
#>
[CmdletBinding()]
param (
    [Parameter(Mandatory = $true)]
    [ValidateSet("Operator", "Client")]
    [string]$Role,
    [string]$OperatorPeerId,
    [string]$NodeName = $env:COMPUTERNAME,
    [switch]$ForceRoleChange
)

$ErrorActionPreference = "Stop"
$ServiceName = "FortiqService"
$LegacyServiceName = "Fortiq"
$InstallDir = "C:\Program Files\FORTIQ"
$DataDir = "C:\ProgramData\FORTIQ"
$ConfigFile = Join-Path $DataDir "fortiq.toml"
$RunKeyPath = "Software\Microsoft\Windows\CurrentVersion\Run"
$RunValueName = "FORTIQ Desktop"

# Windows PowerShell 5.1 turns every stderr line from a native command into a
# NativeCommandError while $ErrorActionPreference is "Stop". The FORTIQ binaries
# write progress to stderr, which aborted the installation with a bare "code 1".
function Invoke-NativeCommand {
    param(
        [Parameter(Mandatory = $true)][string]$FailureMessage,
        [Parameter(Mandatory = $true)][scriptblock]$Command,
        [switch]$IgnoreExitCode
    )
    $previous = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        & $Command 2>&1 | ForEach-Object { Write-Host $_ }
        if (-not $IgnoreExitCode -and $LASTEXITCODE -ne 0) {
            throw "$FailureMessage (exit code $LASTEXITCODE)"
        }
    } finally {
        $ErrorActionPreference = $previous
    }
}

# Removing the program directory fails while a FORTIQ process still holds a
# file. Stopping a process is not instant, so give Windows a moment to release
# the handles instead of failing the whole installation.
function Remove-DirectoryWithRetry([string]$Path) {
    for ($attempt = 1; $attempt -le 10; $attempt++) {
        try {
            Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop
            return
        } catch {
            if ($attempt -lt 10) {
                Start-Sleep -Milliseconds 500
                continue
            }
            # Windows refuses to delete a file that is still mapped, but it does
            # allow renaming one. Move the stragglers aside so the new product
            # can be laid down, and let the next installation sweep them up.
            Write-Host "Fichiers verrouilles detectes, mise de cote..." -ForegroundColor Yellow
            Get-ChildItem -LiteralPath $Path -File -Recurse -ErrorAction SilentlyContinue |
                ForEach-Object {
                    try {
                        Remove-Item -LiteralPath $_.FullName -Force -ErrorAction Stop
                    } catch {
                        $aside = "$($_.FullName).old-$((Get-Date).Ticks)"
                        try {
                            Rename-Item -LiteralPath $_.FullName -NewName (Split-Path -Leaf $aside) -Force -ErrorAction Stop
                        } catch {
                            throw "Impossible de liberer $($_.FullName) : $($_.Exception.Message). Fermez FORTIQ et relancez l'installation."
                        }
                    }
                }
            return
        }
    }
}

function Test-Administrator {
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Test-OperatorPeerId([string]$PeerId) {
    if ([string]::IsNullOrWhiteSpace($PeerId)) { return $false }
    return $PeerId -match '^12D3KooW[1-9A-HJ-NP-Za-km-z]{40,60}$'
}

function Get-InstalledRole([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    if ((Get-Content -Raw -LiteralPath $Path) -match '(?m)^\s*operator_peer_id\s*=') { return "Client" }
    return "Operator"
}

trap {
    Write-Host "ECHEC: $_" -ForegroundColor Red
    Write-Host "Journal complet : $LogFile" -ForegroundColor Yellow
    try { Stop-Transcript | Out-Null } catch { }
    exit 1
}

if (-not (Test-Administrator)) {
    throw "FORTIQ installation requires Administrator privileges. Run the role-specific Setup executable."
}
if ([string]::IsNullOrWhiteSpace($NodeName) -or $NodeName.IndexOfAny([char[]]"`"`r`n") -ge 0) {
    throw "NodeName is empty or contains unsupported characters."
}
if ($Role -eq "Client" -and -not (Test-OperatorPeerId $OperatorPeerId)) {
    throw "Client installation requires a valid Operator PeerId (12D3KooW...)."
}

$existingRole = Get-InstalledRole $ConfigFile
if ($existingRole -and $existingRole -ne $Role -and -not $ForceRoleChange) {
    throw "Existing role is $existingRole. Refusing to change to $Role without -ForceRoleChange."
}

# Record everything: the Setup wizard only surfaces "code 1", which says
# nothing about why an installation stopped.
$LogFile = Join-Path $env:TEMP "FORTIQ-install.log"
try { Start-Transcript -LiteralPath $LogFile -Force | Out-Null } catch { }

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$requiredFiles = @("fortiq-service.exe", "fortiq.exe", "fortiq-desktop.exe", "uninstall.ps1")
foreach ($file in $requiredFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $scriptDir $file))) {
        throw "Incomplete FORTIQ package: required file '$file' is missing."
    }
}

Write-Host "Installing FORTIQ $Role on $NodeName..." -ForegroundColor Cyan
if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath (Join-Path $InstallDir "fortiq-service.exe")) {
        Invoke-NativeCommand -FailureMessage "service uninstall" -IgnoreExitCode -Command {
            & (Join-Path $InstallDir "fortiq-service.exe") service uninstall
        }
    }
}

# Remove every previous FORTIQ Windows generation before laying down the new
# atomic product bundle. ProgramData is intentionally preserved so upgrades keep
# the node identity, role, tickets, and operator authorization.
if (Get-Service -Name $LegacyServiceName -ErrorAction SilentlyContinue) {
    Stop-Service -Name $LegacyServiceName -Force -ErrorAction SilentlyContinue
    & sc.exe delete $LegacyServiceName | Out-Null
}
# Stop by name first: Process.Path is empty for a process this session cannot
# open, so the path filter alone silently skipped the running desktop app and
# the installation then failed on a locked fortiq-desktop.exe.
$fortiqProcessNames = @("fortiq-desktop", "fortiq-service", "fortiq")
foreach ($name in $fortiqProcessNames) {
    Get-Process -Name $name -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
}
Get-Process -ErrorAction SilentlyContinue |
    Where-Object {
        $_.Path -and $_.Path.StartsWith($InstallDir, [StringComparison]::OrdinalIgnoreCase)
    } |
    Stop-Process -Force -ErrorAction SilentlyContinue

foreach ($name in $fortiqProcessNames) {
    foreach ($process in Get-Process -Name $name -ErrorAction SilentlyContinue) {
        try { $process.WaitForExit(5000) | Out-Null } catch { }
    }
}
if (Test-Path -LiteralPath $InstallDir) {
    Remove-DirectoryWithRetry $InstallDir
}

New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
Get-ChildItem -LiteralPath $InstallDir -Filter "*.old-*" -File -ErrorAction SilentlyContinue |
    Remove-Item -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $DataDir -Force | Out-Null
# Harden ProgramData ACL exclusively to SYSTEM (S-1-5-18) and Administrators (S-1-5-32-544).
# Disables inheritance without copying inherited rules, and purges any non-admin ACEs.
$acl = New-Object System.Security.AccessControl.DirectorySecurity
$acl.SetAccessRuleProtection($true, $false)
$systemSid = New-Object System.Security.Principal.SecurityIdentifier("S-1-5-18")
$adminSid = New-Object System.Security.Principal.SecurityIdentifier("S-1-5-32-544")
$inherit = [System.Security.AccessControl.InheritanceFlags]"ContainerInherit, ObjectInherit"
$prop = [System.Security.AccessControl.PropagationFlags]::None
$allow = [System.Security.AccessControl.AccessControlType]::Allow
$fullControl = [System.Security.AccessControl.FileSystemRights]::FullControl
$acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($systemSid, $fullControl, $inherit, $prop, $allow)))
$acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($adminSid, $fullControl, $inherit, $prop, $allow)))
Set-Acl -LiteralPath $DataDir -AclObject $acl

# Verify exclusive ACL: fail-closed if inheritance is enabled or unauthorized SIDs are present.
$securedAcl = Get-Acl -LiteralPath $DataDir
if (-not $securedAcl.AreAccessRulesProtected) {
    throw "Security verification failed: ProgramData ACL inheritance is not disabled on $DataDir."
}
$allowedSids = @("S-1-5-18", "S-1-5-32-544")
$activeRules = $securedAcl.GetAccessRules($true, $false, [System.Security.Principal.SecurityIdentifier])
foreach ($rule in $activeRules) {
    $sid = $rule.IdentityReference.Value
    if ($allowedSids -notcontains $sid) {
        throw "Security verification failed: ProgramData ACL contains unauthorized entry for SID $sid on $DataDir."
    }
}
foreach ($file in $requiredFiles + @("install.ps1")) {
    $source = Join-Path $scriptDir $file
    if (Test-Path -LiteralPath $source) {
        $destination = Join-Path $InstallDir $file
        if ([IO.Path]::GetFullPath($source) -ne [IO.Path]::GetFullPath($destination)) {
            Copy-Item -LiteralPath $source -Destination $destination -Force
        }
    }
}

$authorization = if ($Role -eq "Client") {
    "[authorization]`r`noperator_peer_id = `"$OperatorPeerId`"`r`n"
} else {
    "# Operator role: operator_peer_id is intentionally absent.`r`n[authorization]`r`n"
}
$config = @"
# Generated by FORTIQ $Role Setup. Role changes require explicit confirmation.
[node]
name = "$NodeName"

[identity]
path = "C:/ProgramData/FORTIQ/identity.key"

$authorization
[network]
listen_quic = "0.0.0.0:4001"
relay_peer = "/ip4/51.255.46.58/udp/4001/quic-v1/p2p/12D3KooWRFrWVx2CANXcjXvTNaqkh6APgAecsW94CLwNEg5wsqLy"

[ticket]
path = "C:/ProgramData/FORTIQ/ticket.json"
"@
Set-Content -LiteralPath $ConfigFile -Value $config -Encoding utf8

$machinePath = [Environment]::GetEnvironmentVariable("Path", [EnvironmentVariableTarget]::Machine)
$pathEntries = @($machinePath -split ';' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
if ($pathEntries -notcontains $InstallDir) {
    [Environment]::SetEnvironmentVariable("Path", (($pathEntries + $InstallDir) -join ';'), [EnvironmentVariableTarget]::Machine)
}
$env:Path = "$env:Path;$InstallDir"

Invoke-NativeCommand -FailureMessage "L'installation du service FORTIQ a echoue" -Command {
    & (Join-Path $InstallDir "fortiq-service.exe") service install --config $ConfigFile
}
Invoke-NativeCommand -FailureMessage "Le demarrage du service FORTIQ a echoue" -Command {
    & (Join-Path $InstallDir "fortiq-service.exe") service start
}

# Use the 64-bit registry view explicitly: the NSIS bootstrapper is 32-bit and
# otherwise redirects this value to WOW6432Node, which Windows does not use for
# normal desktop logon startup.
$registryBase = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
    [Microsoft.Win32.RegistryHive]::LocalMachine,
    [Microsoft.Win32.RegistryView]::Registry64
)
$runKey = $registryBase.CreateSubKey($RunKeyPath)
try {
    $runKey.SetValue($RunValueName, ('"{0}"' -f (Join-Path $InstallDir "fortiq-desktop.exe")), [Microsoft.Win32.RegistryValueKind]::String)
} finally {
    $runKey.Dispose()
    $registryBase.Dispose()
}
# Remove the incorrectly redirected value left by pre-fix installers.
$legacyRegistryBase = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
    [Microsoft.Win32.RegistryHive]::LocalMachine,
    [Microsoft.Win32.RegistryView]::Registry32
)
$legacyRunKey = $legacyRegistryBase.OpenSubKey($RunKeyPath, $true)
if ($legacyRunKey) {
    try { $legacyRunKey.DeleteValue($RunValueName, $false) } finally { $legacyRunKey.Dispose() }
}
$legacyRegistryBase.Dispose()

$startMenuDir = Join-Path $env:ProgramData "Microsoft\Windows\Start Menu\Programs\FORTIQ"
New-Item -ItemType Directory -Path $startMenuDir -Force | Out-Null
$shell = New-Object -ComObject WScript.Shell
$desktopShortcut = $shell.CreateShortcut((Join-Path $startMenuDir "FORTIQ.lnk"))
$desktopShortcut.TargetPath = Join-Path $InstallDir "fortiq-desktop.exe"
$desktopShortcut.WorkingDirectory = $InstallDir
$desktopShortcut.Description = "FORTIQ $Role"
$desktopShortcut.Save()
$cliShortcut = $shell.CreateShortcut((Join-Path $startMenuDir "FORTIQ CLI.lnk"))
$cliShortcut.TargetPath = "powershell.exe"
$cliShortcut.Arguments = '-NoExit -Command "fortiq status"'
$cliShortcut.WorkingDirectory = $InstallDir
$cliShortcut.Description = "FORTIQ command line"
$cliShortcut.Save()

$publicDesktop = [Environment]::GetFolderPath("CommonDesktopDirectory")
if ($publicDesktop -and (Test-Path -LiteralPath $publicDesktop)) {
    $publicShortcut = $shell.CreateShortcut((Join-Path $publicDesktop "FORTIQ.lnk"))
    $publicShortcut.TargetPath = Join-Path $InstallDir "fortiq-desktop.exe"
    $publicShortcut.WorkingDirectory = $InstallDir
    $publicShortcut.Description = "FORTIQ $Role"
    $publicShortcut.Save()
}

Write-Host "FORTIQ $Role installation completed." -ForegroundColor Green
Write-Host "Service starts at boot; desktop starts at interactive user logon."
Invoke-NativeCommand -FailureMessage "status" -IgnoreExitCode -Command {
    & (Join-Path $InstallDir "fortiq.exe") status
}

try { Stop-Transcript | Out-Null } catch { }
