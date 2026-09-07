<#
.SYNOPSIS
    Acquires the pinned local assistant model described by models/manifest.json.

.DESCRIPTION
    Fortiq does not run without its assistant, and the model is not committed to the repository: it is
    either placed in the installation package by the build or fetched here, during installation. This
    script downloads exactly the file the manifest pins and verifies its length and SHA-256 before the
    file is put where the application will look for it. A mismatch is fatal and leaves nothing behind.

    The download lands beside its destination and is renamed into place only after it verifies, so an
    interrupted run never leaves a half-written model that looks installed. An already present model
    that matches the manifest is kept, so the script is safe to re-run.
#>
[CmdletBinding()]
param(
    [string] $ManifestPath = '',
    [string] $Name = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $ManifestPath) {
    $root = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
    $ManifestPath = Join-Path $root '..\models\manifest.json'
}

$manifestPath = (Resolve-Path $ManifestPath).Path
$modelRoot = Split-Path $manifestPath -Parent
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json

if ($manifest.schema -ne 'fortiq.model-manifest' -or $manifest.version -ne 1) {
    throw "Unsupported model manifest schema or version."
}

function Test-Sha256 {
    param([string] $Path, [string] $Expected)
    $actual = (Get-FileHash -Path $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    return $actual -eq $Expected.ToLowerInvariant()
}

$acquired = 0

foreach ($entry in $manifest.models) {
    if ($Name -and $entry.name -ne $Name) { continue }

    if ($entry.sourceUrl -notmatch '^https://') {
        throw "Model $($entry.name) is not fetched over HTTPS; refusing to download it."
    }

    $relative = $entry.relativePath -replace '/', [IO.Path]::DirectorySeparatorChar
    $modelPath = Join-Path $modelRoot $relative

    # The manifest is data, and this script writes what it names. Anything that resolves outside the
    # model folder is a damaged or tampered manifest rather than a path worth following.
    $resolvedRoot = [IO.Path]::GetFullPath($modelRoot)
    $resolvedPath = [IO.Path]::GetFullPath($modelPath)
    if (-not $resolvedPath.StartsWith($resolvedRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Model $($entry.name) points outside the model folder; refusing to write it."
    }

    if ((Test-Path $modelPath) -and ((Get-Item $modelPath).Length -eq $entry.fileLength) -and (Test-Sha256 -Path $modelPath -Expected $entry.fileSha256)) {
        Write-Host "$($entry.name) $($entry.version) already present and verified."
        $acquired++
        continue
    }

    New-Item -ItemType Directory -Path (Split-Path $modelPath -Parent) -Force | Out-Null
    $partial = "$modelPath.partial"
    Remove-Item -Force $partial -ErrorAction SilentlyContinue

    try {
        Write-Host "Downloading $($entry.name) $($entry.version) ($([math]::Round($entry.fileLength / 1GB, 2)) GB) from $($entry.sourceUrl)"
        # A model is large enough that the progress bar costs measurable time in PowerShell 5.1.
        $previousProgress = $ProgressPreference
        $ProgressPreference = 'SilentlyContinue'
        try {
            Invoke-WebRequest -Uri $entry.sourceUrl -OutFile $partial -UseBasicParsing
        }
        finally {
            $ProgressPreference = $previousProgress
        }

        $length = (Get-Item $partial).Length
        if ($length -ne $entry.fileLength) {
            throw "Downloaded $length bytes rather than the pinned $($entry.fileLength); the download is incomplete."
        }

        if (-not (Test-Sha256 -Path $partial -Expected $entry.fileSha256)) {
            throw "Model SHA-256 does not match the manifest; refusing to install it."
        }

        Move-Item -Path $partial -Destination $modelPath -Force
        Write-Host "$($entry.name) $($entry.version) verified and installed."
        $acquired++
    }
    finally {
        Remove-Item -Force $partial -ErrorAction SilentlyContinue
    }
}

if ($acquired -eq 0) {
    throw "No model was acquired. Check the manifest at $manifestPath" + $(if ($Name) { " for an entry named '$Name'." } else { "." })
}
