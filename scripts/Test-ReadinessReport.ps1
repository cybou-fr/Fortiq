<#
.SYNOPSIS
    Gates deployment on a fresh preflight report from the expected Windows account.
.DESCRIPTION
    The report is local evidence, not signed attestation. Its directory must be controlled by the
    deployment operator. A console run under an administrator cannot approve a different service SID.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Path,
    [Parameter(Mandatory)] [string] $ExpectedAccountSid,
    [DateTimeOffset] $NotBefore = [DateTimeOffset]::UtcNow.AddMinutes(-5)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$report = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
if ($report.schema -cne 'fortiq.service-readiness' -or $report.version -ne 1) {
    throw 'Unsupported readiness report schema or version.'
}
if ($report.accountSid -cne $ExpectedAccountSid) {
    throw 'The readiness report was produced by a different Windows account.'
}
$produced = if ($report.producedAt -is [DateTime]) { [DateTimeOffset]$report.producedAt }
    else { [DateTimeOffset]::Parse($report.producedAt, [Globalization.CultureInfo]::InvariantCulture) }
if ($produced -lt $NotBefore -or $produced -gt [DateTimeOffset]::UtcNow.AddMinutes(1)) {
    throw 'The readiness report is stale or future-dated.'
}
if ($report.passed -isnot [bool] -or -not $report.passed -or @($report.findings).Count -eq 0) {
    throw 'Preflight did not pass. Review the findings in the report.'
}
foreach ($finding in $report.findings) {
    if ($finding.passed -isnot [bool] -or -not $finding.passed) {
        throw 'A required preflight check did not pass.'
    }
}
foreach ($check in 'state-read', 'state-access', 'engine', 'password-helper', 'active-schedules') {
    if (-not @($report.findings | Where-Object { $_.check -ceq $check }).Count) {
        throw "Required global check is missing: $check"
    }
}
$scheduleFindings = @($report.findings | Where-Object { $null -ne $_.scheduleId })
if (-not $scheduleFindings.Count) { throw 'No per-schedule access checks are present.' }
foreach ($schedule in ($scheduleFindings | Group-Object scheduleId)) {
    foreach ($check in 'source-directory', 'recovery-kit', 'device-key-scope', 'device-unlock', 'repository-access') {
        if (-not @($schedule.Group | Where-Object { $_.check -ceq $check }).Count) {
            throw "Required schedule check is missing: $check"
        }
    }
}
Write-Host "Preflight passed for $($report.accountSid) at $($produced.ToString('O')). This is not a restore proof."
