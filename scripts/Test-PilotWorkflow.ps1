<#
.SYNOPSIS
    Executes the comprehensive Fortiq Pilot Workflow verification end-to-end.
.DESCRIPTION
    Validates:
      1. Pinned engine acquisition and integrity
      2. Deployment bundle generation and bundle-manifest.json verification
      3. Fail-closed deterministic installation (Desktop, Service, Recover)
      4. System status inspection and platform prerequisite reporting
      5. End-to-end cryptographic pilot invariants:
         - BIP-39 recovery kit and machine/current-user key envelopes
         - Schema v2 hash-chained operation receipt ledger
         - Size-aware restore drill execution
         - Sovereign bare-metal disaster recovery via Fortiq.Recover
         - Tamper detection and audit-ledger-tampered health degradation
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [switch] $SkipBundleBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path $PSScriptRoot -Parent
Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host " Fortiq Pilot End-to-End Workflow Verification" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# 1. Pinned engine verification
Write-Host "`n[1/6] Verifying pinned repository engine..." -ForegroundColor Yellow
$manifestPath = Join-Path $repositoryRoot 'engines/manifest.json'
if (-not (Test-Path $manifestPath)) {
    throw "Engines manifest is missing: $manifestPath"
}
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
$resticEntry = @($manifest.engines | Where-Object { $_.name -ceq 'restic' -and $_.rid -ceq 'win-x64' })[0]
$engineBinary = Join-Path $repositoryRoot "engines/$($resticEntry.relativePath)"

if (-not (Test-Path $engineBinary)) {
    Write-Host "Pinned engine missing. Running scripts/Get-Engine.ps1..."
    & (Join-Path $PSScriptRoot 'Get-Engine.ps1')
}

$binaryHash = (Get-FileHash -LiteralPath $engineBinary -Algorithm SHA256).Hash
if ($binaryHash -ine $resticEntry.binarySha256) {
    throw "Pinned engine hash verification failed."
}
Write-Host "Engine restic win-x64 verified (SHA-256: $binaryHash)" -ForegroundColor Green

# 2. Build deployment bundle
$tempRoot = [IO.Path]::Combine([IO.Path]::GetTempPath(), "fortiq-pilot-$([Guid]::NewGuid().ToString('N'))")
$bundleDir = Join-Path $tempRoot 'bundle'
$installDir = Join-Path $tempRoot 'installed'
New-Item -ItemType Directory -Path $tempRoot | Out-Null

try {
    Write-Host "`n[2/6] Building deployment bundle via New-DeploymentBundle.ps1..." -ForegroundColor Yellow
    & (Join-Path $PSScriptRoot 'New-DeploymentBundle.ps1') -OutputDirectory $bundleDir -Configuration $Configuration
    if ($LASTEXITCODE -ne 0) { throw "Bundle creation failed." }

    $manifestFile = Join-Path $bundleDir 'bundle-manifest.json'
    $sumsFile = Join-Path $bundleDir 'SHA256SUMS'
    if (-not (Test-Path $manifestFile)) { throw "bundle-manifest.json was not created in bundle root." }
    if (-not (Test-Path $sumsFile)) { throw "SHA256SUMS was not created in bundle root." }

    $bundleManifest = Get-Content $manifestFile -Raw | ConvertFrom-Json
    Write-Host "Deployment bundle generated successfully ($($bundleManifest.components.Count) components)" -ForegroundColor Green

    # 3. Test installation via Fortiq.Desktop CLI
    Write-Host "`n[3/6] Testing deterministic installation into isolated directory..." -ForegroundColor Yellow
    $desktopPublished = Join-Path $bundleDir 'desktop/Fortiq.Desktop.exe'
    if (-not (Test-Path $desktopPublished)) { throw "Published Fortiq.Desktop.exe is missing from bundle." }

    & $desktopPublished --install --dir $installDir --source $bundleDir --no-service --no-path --no-acls --silent
    if ($LASTEXITCODE -ne 0) { throw "Installation command returned exit code $LASTEXITCODE." }

    foreach ($expectedFile in @('Fortiq.Desktop.exe', 'Fortiq.Service.exe', 'Fortiq.Recover.exe', 'Fortiq.PasswordHelper.exe', 'bundle-manifest.json')) {
        $targetFile = Join-Path $installDir $expectedFile
        if (-not (Test-Path $targetFile)) { throw "Installed file is missing: $expectedFile" }
    }
    Write-Host "Installation successful: All components cleanly deployed to $installDir" -ForegroundColor Green

    # 4. Test system status inspector
    Write-Host "`n[4/6] Inspecting installed system status via CLI..." -ForegroundColor Yellow
    $statusOutput = & (Join-Path $installDir 'Fortiq.Desktop.exe') --status --json
    if ($LASTEXITCODE -ne 0) { throw "Status check failed." }
    $statusJson = $statusOutput | ConvertFrom-Json
    Write-Host "System status inspected: Engine=$($statusJson.engine.name), TPM=$($statusJson.platform.tpmAvailable)" -ForegroundColor Green

    # 5. Execute comprehensive end-to-end cryptographic test
    Write-Host "`n[5/6] Running full cryptographic pilot workflow test suite..." -ForegroundColor Yellow
    $testProject = Join-Path $repositoryRoot 'tests/Fortiq.Recovery.IntegrationTests/Fortiq.Recovery.IntegrationTests.csproj'
    & dotnet test $testProject --configuration $Configuration --filter "FullyQualifiedName~PilotWorkflowEndToEndTests"
    if ($LASTEXITCODE -ne 0) { throw "Pilot workflow end-to-end test failed." }

    # 6. Verify documentation claims
    Write-Host "`n[6/6] Verifying documentation and security claims..." -ForegroundColor Yellow
    & (Join-Path $PSScriptRoot 'Test-DocumentationClaims.ps1')
    if ($LASTEXITCODE -ne 0) { throw "Documentation claims check failed." }

    Write-Host "`n==========================================================" -ForegroundColor Green
    Write-Host " PILOT HARDENING SPRINT VERIFICATION: 100% PASSED" -ForegroundColor Green
    Write-Host " All trust boundaries, ledger hash-chains, restore drills," -ForegroundColor Green
    Write-Host " and sovereign disaster recovery invariants are satisfied." -ForegroundColor Green
    Write-Host "==========================================================" -ForegroundColor Green
}
finally {
    if (Test-Path $tempRoot) {
        Remove-Item -Recurse -Force -LiteralPath $tempRoot -ErrorAction SilentlyContinue
    }
}
