<#
.SYNOPSIS
    Automated smoke test for FORTIQ (Windows PowerShell).
    Validates identity creation, HELLO handshake, remote command execution, and ticket closure.
#>

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RootDir = Split-Path -Parent $ScriptDir

Write-Host "=== FORTIQ Automated Smoke Test (Windows) ===" -ForegroundColor Cyan

# Ensure cargo is on PATH
if (-not (Get-Command cargo -ErrorAction SilentlyContinue)) {
    if (Test-Path "$env:USERPROFILE\.cargo\bin\cargo.exe") {
        $env:PATH = "$env:USERPROFILE\.cargo\bin;$env:PATH"
    }
}

# 1. Build binary
Write-Host "--> Building fortiq-service..." -ForegroundColor Yellow
& cargo build -p fortiq-service
if ($LASTEXITCODE -ne 0) {
    Write-Error "Build failed"
    exit 1
}

$ExePath = Join-Path $RootDir "target\debug\fortiq-service.exe"
$TempDir = Join-Path ([System.IO.Path]::GetTempPath()) ([System.Guid]::NewGuid().ToString())
New-Item -ItemType Directory -Path $TempDir | Out-Null

try {
    $OpPort = 49152
    $ManagedPort = 49153

    $OpIdentity = Join-Path $TempDir "operator.id"
    $ManagedIdentity = Join-Path $TempDir "managed.id"
    $ManagedTicket = Join-Path $TempDir "managed.ticket.json"

    # 2. Extract or generate operator PeerId
    Write-Host "--> Generating operator identity..." -ForegroundColor Yellow
    $OpConfigPath = Join-Path $TempDir "operator.toml"
    @"
[node]
name = "smoke-operator"
[identity]
path = "$($OpIdentity -replace '\\', '/')"
[network]
listen_quic = "127.0.0.1:$OpPort"
"@ | Set-Content -Path $OpConfigPath -Encoding utf8

    # Run operator once to initialize identity and get PeerId
    $InitJob = Start-Process -FilePath $ExePath -ArgumentList "--config `"$OpConfigPath`"" -PassThru -NoNewWindow -RedirectStandardOutput (Join-Path $TempDir "op_init.log") -RedirectStandardError (Join-Path $TempDir "op_init_err.log")
    Start-Sleep -Seconds 1
    Stop-Process -Id $InitJob.Id -Force -ErrorAction SilentlyContinue
    $LogContent = Get-Content (Join-Path $TempDir "op_init.log") -Raw
    $OpPeerIdMatch = [regex]::Match($LogContent, "Local operator PeerId:\s+([^\r\n]+)")
    if (-not $OpPeerIdMatch.Success) {
        Write-Error "Failed to extract Operator PeerId from init output"
        exit 1
    }
    $OpPeerId = $OpPeerIdMatch.Groups[1].Value.Trim()
    Write-Host "Operator PeerId: $OpPeerId" -ForegroundColor Green

    # 3. Create managed config
    Write-Host "--> Configuring managed peer..." -ForegroundColor Yellow
    $ManagedConfigPath = Join-Path $TempDir "managed.toml"
    @"
[node]
name = "smoke-managed"
[identity]
path = "$($ManagedIdentity -replace '\\', '/')"
[authorization]
operator_peer_id = "$OpPeerId"
[network]
listen_quic = "127.0.0.1:$ManagedPort"
[ticket]
path = "$($ManagedTicket -replace '\\', '/')"
"@ | Set-Content -Path $ManagedConfigPath -Encoding utf8

    # 4. Open support ticket on managed peer
    Write-Host "--> Opening support ticket on managed peer..." -ForegroundColor Yellow
    & $ExePath --config $ManagedConfigPath ticket open
    & $ExePath --config $ManagedConfigPath ticket status

    # 5. Start managed node in background
    Write-Host "--> Starting managed node..." -ForegroundColor Yellow
    $ManagedOutLog = Join-Path $TempDir "managed_out.log"
    $ManagedErrLog = Join-Path $TempDir "managed_err.log"
    $ManagedProc = Start-Process -FilePath $ExePath -ArgumentList "--config `"$ManagedConfigPath`"" -PassThru -NoNewWindow -RedirectStandardOutput $ManagedOutLog -RedirectStandardError $ManagedErrLog

    Start-Sleep -Seconds 2

    # Extract managed PeerId
    $ManagedLogContent = Get-Content $ManagedOutLog -Raw
    $ManagedPeerIdMatch = [regex]::Match($ManagedLogContent, "Local PeerId:\s+([^\r\n]+)")
    $ManagedPeerId = $ManagedPeerIdMatch.Groups[1].Value.Trim()
    Write-Host "Managed PeerId: $ManagedPeerId" -ForegroundColor Green

    $ManagedDialAddr = "/ip4/127.0.0.1/udp/$ManagedPort/quic-v1/p2p/$ManagedPeerId"

    # 6. Execute remote shell command test from operator
    Write-Host "--> Testing remote command execution via P2P stream..." -ForegroundColor Yellow
    $CommandOutput = & $ExePath --config $OpConfigPath --dial $ManagedDialAddr --shell $ManagedPeerId --command "whoami"
    Write-Host "Command result: $CommandOutput" -ForegroundColor Green

    # 7. Close ticket remotely as operator
    Write-Host "--> Closing ticket remotely..." -ForegroundColor Yellow
    & $ExePath --config $OpConfigPath ticket close --peer $ManagedPeerId --dial $ManagedDialAddr

    # 8. Verify ticket is closed
    Write-Host "--> Verifying closed ticket state..." -ForegroundColor Yellow
    $StatusAfter = & $ExePath --config $ManagedConfigPath ticket status
    Write-Host "Status: $StatusAfter" -ForegroundColor Green
    if ($StatusAfter -notmatch "CLOSED") {
        Write-Error "Ticket was not closed properly!"
        exit 1
    }

    Write-Host "=== Smoke Test PASSED Successfully! ===" -ForegroundColor Green
}
finally {
    if ($ManagedProc -and -not $ManagedProc.HasExited) {
        Stop-Process -Id $ManagedProc.Id -Force -ErrorAction SilentlyContinue
    }
    Remove-Item -Recurse -Force $TempDir -ErrorAction SilentlyContinue
}
