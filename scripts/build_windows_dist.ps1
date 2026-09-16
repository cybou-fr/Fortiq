<#
.SYNOPSIS
    Builds complete FORTIQ Operator and Client Windows distributions.
#>
[CmdletBinding()]
param (
    [string]$Version = "0.1.0",
    [string]$Target = "x86_64-pc-windows-msvc",
    [switch]$SkipBuild,
    [switch]$SkipNsis,
    [string]$MakeNsisPath
)

$ErrorActionPreference = "Stop"
$rootDir = Split-Path -Parent $PSScriptRoot
$cargoCommand = Get-Command cargo -ErrorAction SilentlyContinue
$cargoPath = if ($cargoCommand) { $cargoCommand.Source } else { Join-Path $env:USERPROFILE ".cargo\bin\cargo.exe" }
if (-not (Test-Path -LiteralPath $cargoPath)) { throw "cargo.exe was not found." }
$desktopDir = Join-Path $rootDir "apps\fortiq-desktop"
$targetReleaseDir = Join-Path $rootDir "target\$Target\release"
$tauriTargetReleaseDir = Join-Path $desktopDir "src-tauri\target\$Target\release"
$distRoot = Join-Path $rootDir "target\dist"
$payloadDir = Join-Path $distRoot "windows-payload"
$installerDir = Join-Path $distRoot "installers"

# Windows PowerShell 5.1 turns every stderr line from a native command into a
# NativeCommandError while $ErrorActionPreference is "Stop", so cargo's ordinary
# progress output aborted the script. Run native tools with the preference
# relaxed and judge them by their exit code instead.
function Invoke-NativeCommand {
    param(
        [Parameter(Mandatory = $true)][string]$FailureMessage,
        [Parameter(Mandatory = $true)][scriptblock]$Command
    )
    $previous = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        & $Command
        if ($LASTEXITCODE -ne 0) { throw $FailureMessage }
    } finally {
        $ErrorActionPreference = $previous
    }
}

if (-not $SkipBuild) {
    Invoke-NativeCommand -FailureMessage "Backend build failed." -Command {
        & $cargoPath build --release --target $Target -p fortiq-service -p fortiq-cli
    }
    Push-Location $desktopDir
    try {
        Invoke-NativeCommand -FailureMessage "Desktop frontend build failed." -Command {
            npm.cmd run build
        }
        Invoke-NativeCommand -FailureMessage "Desktop executable build failed." -Command {
            & $cargoPath build --release --target $Target --manifest-path src-tauri/Cargo.toml
        }
    } finally {
        Pop-Location
    }
}

if (Test-Path -LiteralPath $payloadDir) { Remove-Item -LiteralPath $payloadDir -Recurse -Force }
New-Item -ItemType Directory -Path $payloadDir -Force | Out-Null
New-Item -ItemType Directory -Path $installerDir -Force | Out-Null

$desktopCandidates = @(
    (Join-Path $targetReleaseDir "fortiq-desktop.exe"),
    (Join-Path $tauriTargetReleaseDir "fortiq-desktop.exe"),
    (Join-Path $rootDir "target\release\fortiq-desktop.exe"),
    (Join-Path $desktopDir "src-tauri\target\release\fortiq-desktop.exe")
)
function Select-NewestArtifact([string[]]$Candidates) {
    $files = $Candidates |
        Where-Object { Test-Path -LiteralPath $_ } |
        ForEach-Object { Get-Item -LiteralPath $_ } |
        Sort-Object LastWriteTimeUtc -Descending
    return $files | Select-Object -First 1 -ExpandProperty FullName
}

# `-SkipBuild` is commonly used after a normal host release build. Do not let a
# stale target-specific artifact silently win merely because it appears first.
$desktopExe = Select-NewestArtifact $desktopCandidates
$serviceExe = Select-NewestArtifact @((Join-Path $targetReleaseDir "fortiq-service.exe"), (Join-Path $rootDir "target\release\fortiq-service.exe"))
$cliExe = Select-NewestArtifact @((Join-Path $targetReleaseDir "fortiq.exe"), (Join-Path $rootDir "target\release\fortiq.exe"))
$artifacts = @{
    "fortiq-service.exe" = $serviceExe
    "fortiq.exe" = $cliExe
    "fortiq-desktop.exe" = $desktopExe
    "install.ps1" = Join-Path $rootDir "packaging\windows\install.ps1"
    "uninstall.ps1" = Join-Path $rootDir "packaging\windows\uninstall.ps1"
}
foreach ($entry in $artifacts.GetEnumerator()) {
    if ([string]::IsNullOrWhiteSpace($entry.Value) -or -not (Test-Path -LiteralPath $entry.Value)) {
        throw "Complete Windows product cannot be built: '$($entry.Key)' is missing."
    }
    Copy-Item -LiteralPath $entry.Value -Destination (Join-Path $payloadDir $entry.Key) -Force
}

foreach ($role in @("Operator", "Client")) {
    $zipStage = Join-Path $distRoot "FORTIQ-$role-$Version-Windows-x64"
    if (Test-Path -LiteralPath $zipStage) { Remove-Item -LiteralPath $zipStage -Recurse -Force }
    Copy-Item -LiteralPath $payloadDir -Destination $zipStage -Recurse
    $zipPath = "$zipStage.zip"
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    Compress-Archive -Path (Join-Path $zipStage "*") -DestinationPath $zipPath -Force
    Remove-Item -LiteralPath $zipStage -Recurse -Force
}

if (-not $MakeNsisPath) {
    $nsisCandidates = @(
        "$env:ProgramFiles\NSIS\makensis.exe",
        "${env:ProgramFiles(x86)}\NSIS\makensis.exe",
        # Tauri downloads its own NSIS for `tauri build`; reuse it rather than
        # requiring a second system-wide install.
        "$env:LOCALAPPDATA\tauri\NSIS\Bin\makensis.exe"
    )
    $command = Get-Command makensis.exe -ErrorAction SilentlyContinue
    if ($command) { $MakeNsisPath = $command.Source }
    if (-not $MakeNsisPath) {
        $MakeNsisPath = $nsisCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    }
}
if ($SkipNsis -or -not $MakeNsisPath -or -not (Test-Path -LiteralPath $MakeNsisPath)) {
    Write-Host "NSIS makensis.exe introuvable ou -SkipNsis specifie : passage des installeurs Setup .exe." -ForegroundColor Yellow
    Write-Host "Les distributions autonomes ZIP sont pretes dans : $distRoot" -ForegroundColor Green
    return
}

$numericParts = @([regex]::Matches($Version, '\d+') | ForEach-Object { $_.Value })
while ($numericParts.Count -lt 4) { $numericParts += "0" }
$versionNum = ($numericParts | Select-Object -First 4) -join '.'
$nsiScript = Join-Path $rootDir "packaging\windows\fortiq-product.nsi"
Push-Location $installerDir
try {
    foreach ($role in @("Operator", "Client")) {
        & $MakeNsisPath "/DVERSION=$Version" "/DVERSION_NUM=$versionNum" "/DPACKAGE_ROLE=$role" "/DDISTDIR=$payloadDir" "/DSRCDIR=$rootDir" "/DOUTDIR=$installerDir" $nsiScript
        if ($LASTEXITCODE -ne 0) { throw "NSIS failed for role $role." }
    }
} finally {
    Pop-Location
}

$expected = @(
    (Join-Path $installerDir "FORTIQ-Operator-Setup-$Version-x64.exe"),
    (Join-Path $installerDir "FORTIQ-Client-Setup-$Version-x64.exe")
)
foreach ($file in $expected) {
    if (-not (Test-Path -LiteralPath $file)) { throw "Expected installer was not produced: $file" }
}
Write-Host "Built complete Operator and Client installers in $installerDir" -ForegroundColor Green
