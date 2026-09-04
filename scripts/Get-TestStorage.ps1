<#
.SYNOPSIS
    Acquires the local S3 server the storage tests run against.

.DESCRIPTION
    Verifies the SHA-256 of what it downloads against test-assets/storage/manifest.json, the same way
    the repository engine is acquired. The server is a test dependency only: it is never shipped, and
    no product code depends on it. Tests skip when it is absent rather than pretending to have tested
    object storage.
#>
[CmdletBinding()]
param(
    [string] $ManifestPath = '',
    [string] $ToolsRoot = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $ManifestPath) {
    $ManifestPath = Join-Path (Split-Path $root -Parent) 'test-assets/storage/manifest.json'
}
if (-not $ToolsRoot) {
    $ToolsRoot = Join-Path (Split-Path $root -Parent) 'tools'
}

$manifest = Get-Content (Resolve-Path $ManifestPath).Path -Raw | ConvertFrom-Json
if ($manifest.schema -ne 'fortiq.test-storage-manifest' -or $manifest.version -ne 1) {
    throw 'Unsupported test storage manifest schema or version.'
}

foreach ($server in $manifest.servers) {
    $path = Join-Path $ToolsRoot ($server.relativePath -replace '/', [IO.Path]::DirectorySeparatorChar)

    if (Test-Path $path) {
        $existing = (Get-FileHash -Path $path -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($existing -eq $server.sha256.ToLowerInvariant()) {
            Write-Host "$($server.name) $($server.release) already present and verified."
            continue
        }

        Remove-Item -LiteralPath $path -Force
    }

    New-Item -ItemType Directory -Path (Split-Path $path -Parent) -Force | Out-Null
    $download = "$path.partial"

    Write-Host "Downloading $($server.sourceUrl)"
    Invoke-WebRequest -Uri $server.sourceUrl -OutFile $download -UseBasicParsing

    $actual = (Get-FileHash -Path $download -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $server.sha256.ToLowerInvariant()) {
        Remove-Item -LiteralPath $download -Force
        throw "$($server.name) SHA-256 does not match the manifest; refusing to keep it."
    }

    Move-Item -LiteralPath $download -Destination $path -Force
    Write-Host "$($server.name) $($server.release) verified and installed."
}
