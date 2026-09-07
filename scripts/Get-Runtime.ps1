<#
.SYNOPSIS
    Acquires the pinned llama.cpp inference runtime described by runtimes/manifest.json.

.DESCRIPTION
    Fortiq runs the assistant's model in a separate llama-server process. That runtime is not
    committed: it ships in the installation package or is fetched here.

    The archive SHA-256 is verified before anything is extracted. That is the point at which the
    whole tree is still one object, and it is the only point where a single hash means anything -
    llama-server.exe is a small launcher, and the code that matters is in the DLLs beside it.

    Extraction goes to a temporary folder and is moved into place only once it has verified, so an
    interrupted run never leaves a half-extracted runtime that looks installed. An already present
    runtime is kept, so the script is safe to re-run.
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
    $ManifestPath = Join-Path $root '..\runtimes\manifest.json'
}

$manifestPath = (Resolve-Path $ManifestPath).Path
$runtimeRoot = Split-Path $manifestPath -Parent
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json

if ($manifest.schema -ne 'fortiq.runtime-manifest' -or $manifest.version -ne 1) {
    throw "Unsupported runtime manifest schema or version."
}

function Test-Sha256 {
    param([string] $Path, [string] $Expected)
    $actual = (Get-FileHash -Path $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    return $actual -eq $Expected.ToLowerInvariant()
}

$acquired = 0

foreach ($entry in $manifest.runtimes) {
    if ($entry.rid -ne $Rid) { continue }

    if ($entry.sourceUrl -notmatch '^https://') {
        throw "Runtime $($entry.name) is not fetched over HTTPS; refusing to download it."
    }

    $relative = $entry.relativePath -replace '/', [IO.Path]::DirectorySeparatorChar
    $serverPath = [IO.Path]::GetFullPath((Join-Path $runtimeRoot $relative))
    $resolvedRoot = [IO.Path]::GetFullPath($runtimeRoot)
    if (-not $serverPath.StartsWith($resolvedRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Runtime $($entry.name) points outside the runtime folder; refusing to write it."
    }

    $installDir = Split-Path $serverPath -Parent

    if (Test-Path $serverPath) {
        Write-Host "$($entry.name) $($entry.version) ($Rid) already present."
        $acquired++
        continue
    }

    $work = Join-Path ([IO.Path]::GetTempPath()) ("fortiq-runtime-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $work | Out-Null
    try {
        $archive = Join-Path $work 'runtime.zip'
        Write-Host "Downloading $($entry.name) $($entry.version) ($Rid) from $($entry.sourceUrl)"
        $previousProgress = $ProgressPreference
        $ProgressPreference = 'SilentlyContinue'
        try {
            Invoke-WebRequest -Uri $entry.sourceUrl -OutFile $archive -UseBasicParsing
        }
        finally {
            $ProgressPreference = $previousProgress
        }

        $length = (Get-Item $archive).Length
        if ($length -ne $entry.archiveLength) {
            throw "Downloaded $length bytes rather than the pinned $($entry.archiveLength); the download is incomplete."
        }

        if (-not (Test-Sha256 -Path $archive -Expected $entry.archiveSha256)) {
            throw "Archive SHA-256 does not match the manifest; refusing to extract."
        }

        $extracted = Join-Path $work 'extracted'
        Expand-Archive -Path $archive -DestinationPath $extracted -Force

        $server = Join-Path $extracted (Split-Path $relative -Leaf)
        if (-not (Test-Path $server)) {
            throw "The archive does not contain $(Split-Path $relative -Leaf); this is not the runtime the manifest describes."
        }

        # Into place only now, and as one move, so a folder that exists is a folder that verified.
        New-Item -ItemType Directory -Path (Split-Path $installDir -Parent) -Force | Out-Null
        Move-Item -Path $extracted -Destination $installDir
        Write-Host "$($entry.name) $($entry.version) ($Rid) verified and installed."
        $acquired++
    }
    finally {
        Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
    }
}

if ($acquired -eq 0) {
    throw "No runtime was acquired for '$Rid'. Check the manifest at $manifestPath."
}
