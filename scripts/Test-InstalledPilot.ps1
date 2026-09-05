<#
.SYNOPSIS
    Runs the installed-Windows pilot lane, and fails when it did not actually run.

.DESCRIPTION
    InstalledWindowsPilotTests covers what the core lane never touches: an elevated installation with
    real ACLs, and service registration through the SCM. It needs an elevated session, so it skips
    where it cannot run - and a skipped test exits zero.

    That is the whole reason this script exists. A lane that silently does nothing reports the same
    green as one that passed, and the boundaries it was written to guard reach a pilot machine
    untested with a passing build behind them. The result file is read and a run in which nothing
    executed is a failure, not a pass.

    Run this from an elevated session. On a hosted Windows runner the session is already elevated.

.PARAMETER Configuration
    Build configuration. Defaults to Release, which is what CI publishes from.

.PARAMETER ResultsDirectory
    Where the .trx result file is written. Defaults to artifacts/installed-pilot.
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $ResultsDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$testProject = Join-Path $root 'tests/Fortiq.Recovery.IntegrationTests'

if (-not $ResultsDirectory) {
    $ResultsDirectory = Join-Path $root 'artifacts/installed-pilot'
}

$elevated = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $elevated) {
    # Said before the run rather than after, so the reason is visible when someone starts it by hand
    # and it is not mistaken for a product failure.
    throw ('This lane installs, sets ACLs and registers a service, so it needs an elevated session. ' +
        'The current session is not elevated, and every test would skip.')
}

New-Item -ItemType Directory -Force -Path $ResultsDirectory | Out-Null

& dotnet test $testProject --configuration $Configuration `
    --filter 'FullyQualifiedName~InstalledWindowsPilotTests' `
    --results-directory $ResultsDirectory --logger 'trx;LogFileName=installed-pilot.trx'

$testExitCode = $LASTEXITCODE

$trx = Join-Path $ResultsDirectory 'installed-pilot.trx'
if (-not (Test-Path -LiteralPath $trx)) {
    throw 'The installed pilot lane produced no result file, so nothing can be said about whether it ran.'
}

[xml] $results = Get-Content -LiteralPath $trx
$counters = $results.TestRun.ResultSummary.Counters
$passed = [int] $counters.passed
$failed = [int] $counters.failed
$total = [int] $counters.total
$skipped = $total - [int] $counters.executed

if ($failed -gt 0 -or $testExitCode -ne 0) {
    throw "The installed pilot lane failed: $failed of $total test(s) did not pass."
}

if ($skipped -gt 0) {
    throw ("The installed pilot lane skipped $skipped of $total test(s). A lane that does not run is " +
        'not a lane that passed, and these are the boundaries a pilot machine actually has. ' +
        'Check the skip reasons in the result file.')
}

if ($passed -lt 1) {
    throw 'The installed pilot lane reported no passing tests.'
}

Write-Host ''
Write-Host '==========================================================' -ForegroundColor Green
Write-Host " INSTALLED WINDOWS PILOT LANE: $passed test(s) PASSED" -ForegroundColor Green
Write-Host ' Verified here: elevated installation with real ACLs, the' -ForegroundColor Green
Write-Host ' state directory closed to ordinary users, and service' -ForegroundColor Green
Write-Host ' registration through the SCM.' -ForegroundColor Green
Write-Host '==========================================================' -ForegroundColor Green
Write-Host ''
Write-Host ' Still NOT verified anywhere, and required before a pilot:' -ForegroundColor Yellow
Write-Host '   - a reboot, and the service returning by itself' -ForegroundColor Yellow
Write-Host '   - IPC refused across a pipe to a genuinely unelevated' -ForegroundColor Yellow
Write-Host '     caller (covered only against the policy, not the pipe)' -ForegroundColor Yellow
Write-Host '   - a machine that has never held Fortiq state' -ForegroundColor Yellow
