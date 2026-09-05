<#
.SYNOPSIS
    Publishes the Fortiq tools that ship, and the evidence that says what they are.

.DESCRIPTION
    Produces, under the output directory:

      recover/            the recovery tool, its password helper and their dependencies
      sbom.json           a CycloneDX bill of materials for the whole solution
      SHA256SUMS          the hash of every published file

    The hashes are what a later verification compares against, and what release provenance is
    attested over. Signing is not done here: it needs a certificate this repository does not hold,
    and a build that is not signed must say so rather than look signed.
#>
[CmdletBinding()]
param(
    [string] $OutputDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts/release'),
    [string] $Configuration = 'Release',
    [string] $Runtime = 'win-x64'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path $PSScriptRoot -Parent
$solution = Join-Path $repositoryRoot 'Fortiq.sln'
$recoverProject = Join-Path $repositoryRoot 'src/Fortiq.Recover/Fortiq.Recover.csproj'

if (Test-Path $OutputDirectory) {
    Remove-Item -Recurse -Force -LiteralPath $OutputDirectory
}

$recoverOutput = Join-Path $OutputDirectory 'recover'
New-Item -ItemType Directory -Path $recoverOutput -Force | Out-Null

Write-Host 'Publishing the recovery tool'
# The recovery tool has to run on a machine that has nothing installed, so it carries its runtime.
dotnet publish $recoverProject `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained true `
    -p:PublishSingleFile=false `
    --output $recoverOutput
if ($LASTEXITCODE -ne 0) { throw 'Publishing the recovery tool failed.' }

Write-Host 'Generating the bill of materials'
$sbomDirectory = Join-Path $OutputDirectory 'sbom'
New-Item -ItemType Directory -Path $sbomDirectory -Force | Out-Null

# Pinned like every other dependency: a bill of materials produced by an unpinned tool describes a
# build nobody can reproduce.
$tools = Join-Path $OutputDirectory 'tools'
dotnet tool install CycloneDX --version 6.2.0 --tool-path $tools | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Installing the SBOM tool failed.' }

& (Join-Path $tools 'dotnet-CycloneDX.exe') $solution --out $sbomDirectory --json --filename 'sbom.json'
if ($LASTEXITCODE -ne 0) { throw 'Generating the bill of materials failed.' }

Write-Host 'Building the Community Deployment Bundle'
$bundleDirectory = Join-Path $OutputDirectory 'bundle'
& (Join-Path $PSScriptRoot 'New-DeploymentBundle.ps1') -OutputDirectory $bundleDirectory -Configuration $Configuration
if ($LASTEXITCODE -ne 0) { throw 'Building deployment bundle failed.' }

Write-Host 'Packaging Fortiq-Community-0.1.0-win-x64.zip'
$communityZip = Join-Path $OutputDirectory 'Fortiq-Community-0.1.0-win-x64.zip'
Compress-Archive -Path "$bundleDirectory\*" -DestinationPath $communityZip -Force

Move-Item (Join-Path $sbomDirectory 'sbom.json') (Join-Path $OutputDirectory 'sbom.json')
Remove-Item -Recurse -Force $sbomDirectory, $tools

Write-Host 'Recording hashes'
$hashes = Get-ChildItem -Path $OutputDirectory -Recurse -File |
    Sort-Object FullName |
    ForEach-Object {
        $relative = $_.FullName.Substring($OutputDirectory.Length).TrimStart('\', '/') -replace '\\', '/'
        "$((Get-FileHash -Path $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $relative"
    }

$hashes | Set-Content -Path (Join-Path $OutputDirectory 'SHA256SUMS') -Encoding utf8

Write-Host "Published $($hashes.Count) files to $OutputDirectory"

# Say plainly what this build is not: an unsigned artifact must not be mistaken for a signed one.
$unsigned = Get-ChildItem -Path $recoverOutput -Filter 'Fortiq.*.exe' |
    Where-Object { (Get-AuthenticodeSignature $_.FullName).Status -ne 'Valid' }

if ($unsigned) {
    Write-Warning "Not Authenticode-signed: $($unsigned.Name -join ', '). Signing is a release gate that is not met yet."
}
