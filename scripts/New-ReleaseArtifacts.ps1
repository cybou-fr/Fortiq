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

# Read, never repeated. The name used to carry 0.1.0 written here by hand while the assemblies
# carried 1.0.0 by default, so the archive and the application it contained disagreed about what
# they were.
$version = & (Join-Path $PSScriptRoot 'Get-FortiqVersion.ps1')
$archiveName = "Fortiq-Community-$($version.Archive)-$Runtime.zip"

Write-Host "Packaging $archiveName"
$communityZip = Join-Path $OutputDirectory $archiveName
if (Test-Path $communityZip) { Remove-Item -LiteralPath $communityZip -Force }

# Written entry by entry, with the names normalised by hand. Neither of the obvious ways works here:
# Compress-Archive stores the separator it found on disk, and ZipFile.CreateFromDirectory on .NET
# Framework - which is what Windows PowerShell runs - uses Path.DirectorySeparatorChar too. Both
# produced 695 entries named "desktop\Avalonia.Base.dll".
#
# The ZIP format requires forward slashes (APPNOTE 4.4.17.1). A tool that follows it reads that name
# as one file with a backslash in it rather than a file inside a folder, so the archive extracts as a
# heap of oddly named files with no directories. For a package whose whole promise is that it opens
# on a machine you have never seen, shipping one only Windows Explorer can read is the wrong defect.
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::Open($communityZip, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    $prefix = (Resolve-Path $bundleDirectory).Path.TrimEnd([char]92) + [char]92
    foreach ($file in Get-ChildItem -LiteralPath $bundleDirectory -Recurse -File | Sort-Object FullName) {
        $name = $file.FullName.Substring($prefix.Length).Replace([char]92, '/')
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $archive, $file.FullName, $name, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
}
finally {
    $archive.Dispose()
}

# Read back before anything downstream trusts it. A packaging step that cannot be checked is a
# packaging step that goes wrong quietly.
$written = [System.IO.Compression.ZipFile]::OpenRead($communityZip)
try {
    $malformed = @($written.Entries | Where-Object { $_.FullName -like '*\*' }).Count
    if ($malformed -gt 0) { throw "$malformed archive entries use backslash separators; this archive will not extract correctly off Windows." }
    Write-Host "Packaged $($written.Entries.Count) entries"
}
finally {
    $written.Dispose()
}

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
