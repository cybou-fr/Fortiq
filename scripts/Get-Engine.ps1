<#
.SYNOPSIS
    Acquires the pinned repository engine binaries described by engines/manifest.json.

.DESCRIPTION
    The engine binary is not committed to the repository. This script downloads exactly the archive
    the manifest pins, verifies the archive SHA-256 before extracting anything, and verifies the
    extracted binary length and SHA-256 afterwards. A mismatch is fatal: nothing is left in place.

    An already present binary that matches the manifest is kept, so the script is safe to re-run.
#>
[CmdletBinding()]
param(
    [string] $ManifestPath = '',
    [string] $Rid = 'win-x64'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $ManifestPath) {
    $root = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
    $ManifestPath = Join-Path $root '..\engines\manifest.json'
}

$manifestPath = (Resolve-Path $ManifestPath).Path
$engineRoot = Split-Path $manifestPath -Parent
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json

if ($manifest.schema -ne 'fortiq.engine-manifest' -or $manifest.version -ne 1) {
    throw "Unsupported engine manifest schema or version."
}

function Test-Sha256 {
    param([string] $Path, [string] $Expected)
    $actual = (Get-FileHash -Path $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    return $actual -eq $Expected.ToLowerInvariant()
}

foreach ($entry in $manifest.engines) {
    if ($entry.rid -ne $Rid) { continue }

    $binaryPath = Join-Path $engineRoot ($entry.relativePath -replace '/', [IO.Path]::DirectorySeparatorChar)

    if ((Test-Path $binaryPath) -and (Test-Sha256 -Path $binaryPath -Expected $entry.binarySha256)) {
        Write-Host "$($entry.name) $($entry.version) ($Rid) already present and verified."
        continue
    }

    $work = Join-Path ([IO.Path]::GetTempPath()) ("fortiq-engine-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $work | Out-Null
    try {
        $archive = Join-Path $work 'engine.zip'
        Write-Host "Downloading $($entry.sourceUrl)"
        Invoke-WebRequest -Uri $entry.sourceUrl -OutFile $archive -UseBasicParsing

        if (-not (Test-Sha256 -Path $archive -Expected $entry.archiveSha256)) {
            throw "Archive SHA-256 does not match the manifest; refusing to extract."
        }

        $extracted = Join-Path $work 'extracted'
        Expand-Archive -Path $archive -DestinationPath $extracted -Force

        # Upstream names the executable after the release, so identity is pinned by the length and
        # SHA-256 checks below rather than by the file name inside the archive.
        $candidates = @(Get-ChildItem -Path $extracted -Recurse -File -Filter '*.exe')
        if ($candidates.Count -ne 1) {
            throw "Expected exactly one executable in the archive, found $($candidates.Count)."
        }

        $candidate = $candidates[0]

        if ($candidate.Length -ne $entry.binaryLength) {
            throw "Binary length $($candidate.Length) does not match the manifest value $($entry.binaryLength)."
        }

        if (-not (Test-Sha256 -Path $candidate.FullName -Expected $entry.binarySha256)) {
            throw "Binary SHA-256 does not match the manifest."
        }

        New-Item -ItemType Directory -Path (Split-Path $binaryPath -Parent) -Force | Out-Null
        Copy-Item -Path $candidate.FullName -Destination $binaryPath -Force
        Write-Host "$($entry.name) $($entry.version) ($Rid) verified and installed."
    }
    finally {
        Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
    }
}
