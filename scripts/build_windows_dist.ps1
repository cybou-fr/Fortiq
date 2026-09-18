[CmdletBinding()]
param(
    [string]$Version = "0.1.0",
    [string]$Target = "",
    [switch]$SkipBuild,
    [switch]$SkipNsis,
    [string]$MakeNsisPath
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$cargoCommand = Get-Command cargo.exe -ErrorAction SilentlyContinue
$cargoPath = if ($cargoCommand) { $cargoCommand.Source } else { Join-Path $env:USERPROFILE ".cargo\bin\cargo.exe" }
$cargoPath = (Resolve-Path $cargoPath -ErrorAction Stop).Path
$release = if ([string]::IsNullOrWhiteSpace($Target)) {
    Join-Path $root "target\release"
} else {
    Join-Path $root "target\$Target\release"
}
$distRoot = Join-Path $root "target\dist"
$stage = Join-Path $distRoot "windows-payload"
$installerDir = Join-Path $distRoot "installers"

function Invoke-NativeCommand([string]$FailureMessage, [scriptblock]$Command) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        & $Command 2>&1 | ForEach-Object { Write-Host $_ }
        if ($LASTEXITCODE -ne 0) { throw $FailureMessage }
    } finally {
        $ErrorActionPreference = $previous
    }
}

Push-Location $root
try {
    if (-not $SkipBuild) {
        Invoke-NativeCommand "cargo build failed" {
            if ([string]::IsNullOrWhiteSpace($Target)) {
                & $cargoPath build --release -p fortiq-service -p fortiq-cli -p fortiq-desktop
            } else {
                & $cargoPath build --release --target $Target -p fortiq-service -p fortiq-cli -p fortiq-desktop
            }
        }
    }

    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $stage,$installerDir | Out-Null
    foreach ($file in @("fortiq-service.exe", "fortiq.exe", "fortiq-desktop.exe")) {
        $source = Join-Path $release $file
        if (-not (Test-Path $source)) { throw "Required release artifact missing: $source" }
        Copy-Item $source $stage -Force
    }
    Copy-Item (Join-Path $root "packaging\windows\install.ps1") $stage -Force
    Copy-Item (Join-Path $root "packaging\windows\uninstall.ps1") $stage -Force

    @"
[node]
name = "FORTIQ Node"

[identity]
path = "C:/ProgramData/FORTIQ/identity.key"

[network]
listen_quic = "0.0.0.0:4001"
# relay_peer = "/ip4/RELAY_IPV4/udp/4001/quic-v1/p2p/RELAY_PEER_ID"
# public_addr = "/ip4/PUBLIC_IPV4/udp/4001/quic-v1"

[capabilities]
dcutr = true
relay = false
rendezvous = false
relay_rate_limit = true

[ticket]
path = "C:/ProgramData/FORTIQ/tickets.db"

[ipc]
pipe = "\\\\.\\pipe\\fortiq-ipc"
terminal_pipe = "\\\\.\\pipe\\fortiq-terminal"
"@ | Set-Content (Join-Path $stage "fortiq.toml") -Encoding ascii

    $zipPath = Join-Path $distRoot "FORTIQ-$Version-Windows-x64.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zipPath -Force

    if (-not $SkipNsis) {
        if (-not $MakeNsisPath) {
            $command = Get-Command makensis.exe -ErrorAction SilentlyContinue
            if ($command) { $MakeNsisPath = $command.Source }
        }
        if (-not $MakeNsisPath) {
            $candidates = @(
                "$env:ProgramFiles\NSIS\makensis.exe",
                "${env:ProgramFiles(x86)}\NSIS\makensis.exe",
                "$env:LOCALAPPDATA\tauri\NSIS\Bin\makensis.exe"
            )
            $MakeNsisPath = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
        }
        if (-not $MakeNsisPath -or -not (Test-Path $MakeNsisPath)) {
            Write-Warning "makensis.exe not found; ZIP distributions were created. Install NSIS to build Setup EXE files."
        } else {
            Invoke-NativeCommand "NSIS build failed" {
                & $MakeNsisPath "/DVERSION=$Version" "/DSTAGE=$stage" "/DOUTDIR=$installerDir" (Join-Path $root "packaging\windows\fortiq.nsi")
            }
        }
    }

    Write-Host "Staged: $stage"
    Write-Host "Distributions: $distRoot"
}
finally {
    Pop-Location
}
