[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$testRoot = Join-Path $temporaryRoot ('fortiq-readiness-gate-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$reportPath = Join-Path $testRoot 'report.json'
$gate = Join-Path $PSScriptRoot 'Test-ReadinessReport.ps1'
$findings = @('state-read', 'state-access', 'engine', 'password-helper', 'active-schedules') | ForEach-Object {
    @{ check = $_; scheduleId = $null; passed = $true; detail = 'test' }
}
$findings += @('source-directory', 'recovery-kit', 'device-key-scope', 'device-unlock', 'repository-access') | ForEach-Object {
    @{ check = $_; scheduleId = 'test'; passed = $true; detail = 'test' }
}
$report = @{ schema = 'fortiq.service-readiness'; version = 1; accountSid = 'S-1-5-18'; passed = $true;
    producedAt = [DateTimeOffset]::UtcNow.ToString('O'); findings = $findings }
function Assert-Rejected([string] $name) {
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath
    $rejected = $false
    try { & $gate -Path $reportPath -ExpectedAccountSid 'S-1-5-18' }
    catch { $rejected = $true }
    if (-not $rejected) { throw "Gate accepted invalid report: $name" }
}
try {
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath
    & $gate -Path $reportPath -ExpectedAccountSid 'S-1-5-18'
    $report.accountSid = 'S-1-5-19'; Assert-Rejected 'different account'; $report.accountSid = 'S-1-5-18'
    $report.producedAt = [DateTimeOffset]::UtcNow.AddHours(-1).ToString('O'); Assert-Rejected 'stale'
    $report.producedAt = [DateTimeOffset]::UtcNow.AddHours(1).ToString('O'); Assert-Rejected 'future'
    $report.producedAt = [DateTimeOffset]::UtcNow.ToString('O')
    $report.passed = 'false'; Assert-Rejected 'string instead of boolean'; $report.passed = $true
    $report.findings[0].passed = $false; Assert-Rejected 'failed check hidden by summary'; $report.findings[0].passed = $true
    $report.findings = @($report.findings | Where-Object { $_.check -ne 'repository-access' }); Assert-Rejected 'missing repository check'
    Write-Host 'Readiness gate: 7 cases passed.'
}
finally {
    $resolved = [IO.Path]::GetFullPath($testRoot)
    if (-not $resolved.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe test cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
