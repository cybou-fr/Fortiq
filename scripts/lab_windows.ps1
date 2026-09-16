[CmdletBinding()]
param(
    [ValidateSet("start", "stop", "status", "open", "test", "launch")]
    [string]$Action = "status",
    [switch]$NoAutoOpen
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
$LabRoot = Join-Path $RepoRoot "target\lab\windows"
$ServiceExe = Join-Path $RepoRoot "target\release\fortiq-service.exe"
$CliExe = Join-Path $RepoRoot "target\release\fortiq.exe"
$DesktopExe = Join-Path $RepoRoot "apps\fortiq-desktop\src-tauri\target\x86_64-pc-windows-msvc\release\fortiq-desktop.exe"
$Relay = "/ip4/51.255.46.58/udp/4001/quic-v1/p2p/12D3KooWRFrWVx2CANXcjXvTNaqkh6APgAecsW94CLwNEg5wsqLy"

$Nodes = @{
    operator = @{
        Dir = Join-Path $LabRoot "operator"; Port = 4101
        Pipe = "\\.\pipe\fortiq-lab-operator"
        TerminalPipe = "\\.\pipe\fortiq-lab-operator-terminal"
    }
    client = @{
        Dir = Join-Path $LabRoot "client"; Port = 4102
        Pipe = "\\.\pipe\fortiq-lab-client"
        TerminalPipe = "\\.\pipe\fortiq-lab-client-terminal"
    }
}

function Require-Binaries {
    foreach ($path in @($ServiceExe, $CliExe)) {
        if (-not (Test-Path -LiteralPath $path)) {
            throw "Missing $path. Run: cargo build --release -p fortiq-service -p fortiq-cli"
        }
    }
}

function Node-Path([string]$Role, [string]$Name) {
    return Join-Path $Nodes[$Role].Dir $Name
}

function Get-NodeProcess([string]$Role) {
    $pidPath = Node-Path $Role "service.pid"
    if (-not (Test-Path -LiteralPath $pidPath)) { return $null }
    $savedPid = (Get-Content -Raw -LiteralPath $pidPath).Trim()
    if ($savedPid -notmatch '^\d+$') { return $null }
    return Get-Process -Id ([int]$savedPid) -ErrorAction SilentlyContinue
}

function Invoke-NodeCli([string]$Role, [string[]]$Arguments) {
    & $CliExe --pipe $Nodes[$Role].Pipe --terminal-pipe $Nodes[$Role].TerminalPipe @Arguments
    if ($LASTEXITCODE -ne 0) { throw "FORTIQ $Role CLI failed with exit code $LASTEXITCODE" }
}

function Write-OperatorConfig {
    $role = "operator"
    New-Item -ItemType Directory -Force -Path $Nodes[$role].Dir | Out-Null
    $identity = (Node-Path $role "identity.key").Replace('\', '/')
    $ticket = (Node-Path $role "ticket.json").Replace('\', '/')
    $config = @"
[node]
name = "$env:COMPUTERNAME-LAB-OPERATOR"
[identity]
path = "$identity"
[authorization]
[network]
listen_quic = "0.0.0.0:$($Nodes[$role].Port)"
relay_peer = "$Relay"
[ticket]
path = "$ticket"
[ipc]
pipe = '$($Nodes[$role].Pipe)'
terminal_pipe = '$($Nodes[$role].TerminalPipe)'
"@
    Set-Content -LiteralPath (Node-Path $role "fortiq.toml") -Value $config -Encoding utf8
}

function Write-ClientConfig([string]$OperatorPeerId) {
    $role = "client"
    New-Item -ItemType Directory -Force -Path $Nodes[$role].Dir | Out-Null
    $autoOpen = if ($NoAutoOpen) { "false" } else { "true" }
    $identity = (Node-Path $role "identity.key").Replace('\', '/')
    $ticket = (Node-Path $role "ticket.json").Replace('\', '/')
    $config = @"
[node]
name = "$env:COMPUTERNAME-LAB-CLIENT"
[identity]
path = "$identity"
[authorization]
operator_peer_id = "$OperatorPeerId"
[network]
listen_quic = "0.0.0.0:$($Nodes[$role].Port)"
relay_peer = "$Relay"
[ticket]
path = "$ticket"
auto_open = $autoOpen
[ipc]
pipe = '$($Nodes[$role].Pipe)'
terminal_pipe = '$($Nodes[$role].TerminalPipe)'
"@
    Set-Content -LiteralPath (Node-Path $role "fortiq.toml") -Value $config -Encoding utf8
}

function Start-Node([string]$Role) {
    if (Get-NodeProcess $Role) { return }
    $config = Node-Path $Role "fortiq.toml"
    $stdout = Node-Path $Role "service.log"
    $stderr = Node-Path $Role "service.err.log"
    $process = Start-Process -FilePath $ServiceExe -ArgumentList @("--config", $config) -RedirectStandardOutput $stdout -RedirectStandardError $stderr -WindowStyle Hidden -PassThru
    Set-Content -LiteralPath (Node-Path $Role "service.pid") -Value $process.Id -Encoding ascii
    Start-Sleep -Milliseconds 900
    if (-not (Get-Process -Id $process.Id -ErrorAction SilentlyContinue)) {
        throw "FORTIQ $Role exited during startup. See $stderr"
    }
}

function Get-NodePeerId([string]$Role) {
    $output = Invoke-NodeCli $Role @("id") | Out-String
    $match = [regex]::Match($output, "PeerId:\s+([1-9A-HJ-NP-Za-km-z]+)")
    if (-not $match.Success) { throw "Cannot read $Role PeerId from: $output" }
    return $match.Groups[1].Value
}

function Start-Lab {
    Require-Binaries
    Write-OperatorConfig
    Start-Node "operator"
    $operatorPeerId = Get-NodePeerId "operator"
    Write-ClientConfig $operatorPeerId
    Start-Node "client"
    Write-Host "FORTIQ lab is running." -ForegroundColor Green
    Write-Host "Operator PeerId: $operatorPeerId"
    Write-Host "Client PeerId:   $(Get-NodePeerId 'client')"
}

function Stop-Lab {
    foreach ($role in @("client", "operator")) {
        $process = Get-NodeProcess $role
        if ($process) {
            Stop-Process -Id $process.Id
            $process.WaitForExit(5000) | Out-Null
        }
        Remove-Item -LiteralPath (Node-Path $role "service.pid") -Force -ErrorAction SilentlyContinue
    }
    Write-Host "FORTIQ lab stopped." -ForegroundColor Green
}

function Launch-Desktop([string]$Role) {
    if (-not (Test-Path -LiteralPath $DesktopExe)) { throw "Missing Desktop binary: $DesktopExe" }
    Start-Process -FilePath $DesktopExe -Environment @{
        FORTIQ_PIPE = $Nodes[$Role].Pipe
        FORTIQ_TERMINAL_PIPE = $Nodes[$Role].TerminalPipe
    }
}

switch ($Action) {
    "start" { Start-Lab }
    "stop" { Stop-Lab }
    "status" {
        foreach ($role in @("operator", "client")) {
            Write-Host "--- $role" -ForegroundColor Cyan
            if (Get-NodeProcess $role) { Invoke-NodeCli $role @("status") } else { Write-Host "STOPPED" }
        }
    }
    "open" { Invoke-NodeCli "client" @("ticket", "open") }
    "test" {
        if (-not (Get-NodeProcess "operator") -or -not (Get-NodeProcess "client")) { Start-Lab }
        $clientPeerId = Get-NodePeerId "client"
        Start-Sleep -Seconds 2
        $directAddress = "/ip4/127.0.0.1/udp/$($Nodes.client.Port)/quic-v1/p2p/$clientPeerId"
        Invoke-NodeCli "operator" @("shell", $clientPeerId, "--dial", $directAddress, "--command", "Write-Output FORTIQ_LOCAL_LAB_OK; hostname; whoami")
    }
    "launch" {
        if (-not (Get-NodeProcess "operator") -or -not (Get-NodeProcess "client")) { Start-Lab }
        Launch-Desktop "operator"
        Launch-Desktop "client"
        Write-Host "Operator and Client Desktop windows launched with isolated IPC." -ForegroundColor Green
    }
}
