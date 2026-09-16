$ErrorActionPreference = "Stop"

Write-Host "=== FORTIQ protocol smoke tests (isolated test harness) ===" -ForegroundColor Cyan
$CargoExe = (Get-Command cargo -ErrorAction SilentlyContinue).Source
if (-not $CargoExe) {
    $CargoExe = Join-Path $env:USERPROFILE ".cargo\bin\cargo.exe"
}
if (-not (Test-Path $CargoExe)) {
    throw "Cargo was not found"
}

& $CargoExe test -p fortiq-p2p --test e2e_two_nodes --test e2e_relay_three_nodes
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
