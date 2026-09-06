<#
.SYNOPSIS
    Builds self-contained Windows service and emergency recovery folders with the pinned engine.
.DESCRIPTION
    Creates a new output directory only. It never deletes or replaces an existing bundle, installs a
    service, changes ACLs or signs binaries. Preserve SHA256SUMS with the bundle for transfer checks.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $OutputDirectory,
    [string] $Configuration = 'Release'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
$destination = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $destination) { throw 'OutputDirectory must not already exist.' }
$engineRoot = Join-Path $repositoryRoot 'engines'
$manifestPath = Join-Path $engineRoot 'manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$entries = @($manifest.engines | Where-Object { $_.name -ceq 'restic' -and $_.rid -ceq 'win-x64' })
if ($entries.Count -ne 1) { throw 'Exactly one pinned win-x64 engine is required.' }
$entry = $entries[0]
$enginePath = [IO.Path]::GetFullPath((Join-Path $engineRoot $entry.relativePath))
if (-not $enginePath.StartsWith($engineRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The engine path escapes the engine directory.'
}
$engine = Get-Item -LiteralPath $enginePath
if ($engine.Length -ne $entry.binaryLength -or (Get-FileHash -LiteralPath $enginePath -Algorithm SHA256).Hash -ine $entry.binarySha256) {
    throw 'Pinned engine verification failed. Acquire it with scripts/Get-Engine.ps1.'
}
New-Item -ItemType Directory -Path $destination | Out-Null
foreach ($component in @(@{Folder='desktop'; Project='Fortiq.Desktop'}, @{Folder='service'; Project='Fortiq.Service'}, @{Folder='recover'; Project='Fortiq.Recover'})) {
    $componentOutput = Join-Path $destination $component.Folder
    $project = Join-Path $repositoryRoot "src/$($component.Project)/$($component.Project).csproj"
    & dotnet publish $project --configuration $Configuration --runtime win-x64 --self-contained true -p:FortiqPublishRuntime=win-x64 -p:PublishSingleFile=false --output $componentOutput
    if ($LASTEXITCODE -ne 0) { throw "Publishing $($component.Project) failed; this bundle is incomplete." }
    # A helper reached through a library reference can have a framework-dependent runtimeconfig.
    # Publish it explicitly so it, too, runs without an installed .NET runtime.
    $helperProject = Join-Path $repositoryRoot 'src/Fortiq.PasswordHelper/Fortiq.PasswordHelper.csproj'
    & dotnet publish $helperProject --configuration $Configuration --runtime win-x64 --self-contained true -p:FortiqPublishRuntime=win-x64 -p:PublishSingleFile=false --output $componentOutput
    if ($LASTEXITCODE -ne 0) { throw 'Publishing the self-contained helper failed; this bundle is incomplete.' }
    if (-not (Test-Path -LiteralPath (Join-Path $componentOutput 'Fortiq.PasswordHelper.exe'))) {
        throw 'The published password helper is missing; this bundle is incomplete.'
    }
    # Debug symbols do not ship. They were 101 MB of a 463 MB bundle - almost a quarter of what
    # somebody downloads - and nobody in the field can use them. They also carry the build machine's
    # own layout: every .pdb embedded the developer's home directory as the source root, published
    # to whoever opens the archive.
    Get-ChildItem -LiteralPath $componentOutput -Filter '*.pdb' -File | Remove-Item -Force

    $engineOutput = Join-Path $componentOutput 'engines'
    $binaryOutput = Join-Path $engineOutput $entry.relativePath
    New-Item -ItemType Directory -Path (Split-Path $binaryOutput -Parent) -Force | Out-Null
    Copy-Item -LiteralPath $enginePath -Destination $binaryOutput
    Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $engineOutput 'manifest.json')
    if ((Get-FileHash -LiteralPath $binaryOutput -Algorithm SHA256).Hash -ine $entry.binarySha256) {
        throw 'The copied engine failed verification; this bundle is incomplete.'
    }
}

# Once, into the bundle root - not once per component. The brace above was lost, which put every line
# from here to the end of the file inside the component loop and left the script unparseable, so no
# bundle could be built at all.
# The bundle has to stand on its own: somebody who unzips it on a machine with no network, no
# repository checkout and no Fortiq installed must be able to read what it is, what they may do with
# it, and how to get their files back. Each of these is required rather than optional - a package
# that silently ships without its licence or its recovery guide is not one to release.
foreach ($document in @('SECURITY.md', 'README-FIRST.txt', 'LICENSE', 'RECOVERY-GUIDE.md')) {
    $source = Join-Path $repositoryRoot $document
    if (-not (Test-Path -LiteralPath $source)) {
        throw "'$document' is missing from the repository; the bundle would ship without it."
    }

    Copy-Item -LiteralPath $source -Destination $destination
}

$desktopExe = Join-Path $destination 'desktop/Fortiq.Desktop.exe'
$serviceExe = Join-Path $destination 'service/Fortiq.Service.exe'
$recoverExe = Join-Path $destination 'recover/Fortiq.Recover.exe'
$helperExe = Join-Path $destination 'desktop/Fortiq.PasswordHelper.exe'

# 'version' is the manifest format's version and stays 1.0 until the format changes.
# 'productVersion' is which Fortiq this bundle holds, read from the one place that decides it - two
# different questions that were previously answered by one field, and only by accident.
$version = & (Join-Path $PSScriptRoot 'Get-FortiqVersion.ps1')

$bundleManifest = [ordered]@{
    schema = 'fortiq.bundle-manifest'
    version = '1.0'
    productVersion = $version.Full
    rid = 'win-x64'
    created = (Get-Date).ToUniversalTime().ToString('O')
    components = @(
        [ordered]@{
            name = 'desktop'
            folder = 'desktop'
            mainExecutable = 'desktop/Fortiq.Desktop.exe'
            required = $true
            sha256 = (Get-FileHash -LiteralPath $desktopExe -Algorithm SHA256).Hash.ToLowerInvariant()
        },
        [ordered]@{
            name = 'service'
            folder = 'service'
            mainExecutable = 'service/Fortiq.Service.exe'
            required = $true
            sha256 = (Get-FileHash -LiteralPath $serviceExe -Algorithm SHA256).Hash.ToLowerInvariant()
        },
        [ordered]@{
            name = 'recover'
            folder = 'recover'
            mainExecutable = 'recover/Fortiq.Recover.exe'
            required = $true
            sha256 = (Get-FileHash -LiteralPath $recoverExe -Algorithm SHA256).Hash.ToLowerInvariant()
        },
        [ordered]@{
            name = 'passwordHelper'
            folder = 'desktop'
            mainExecutable = 'desktop/Fortiq.PasswordHelper.exe'
            required = $true
            sha256 = (Get-FileHash -LiteralPath $helperExe -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    )
}
# Every file that will be installed, not just the four executables. A bundle whose Fortiq.Service.exe
# hashes correctly and whose Fortiq.Operations.dll does not is a bundle the installer used to accept:
# the EXE is the thing that gets checked, and the DLL is the thing that runs. Everything the installer
# copies therefore has to sit inside one integrity boundary.
#
# The manifest carries the list rather than SHA256SUMS carrying it, because a manifest that pointed at
# a separate file of hashes would need that file's own hash, and the file lists the manifest. The list
# lives in one place and the cycle does not arise.
$payload = Get-ChildItem -LiteralPath $destination -Recurse -File |
    Where-Object { $_.Name -ne 'bundle-manifest.json' -and $_.Name -ne 'SHA256SUMS' } |
    Sort-Object FullName |
    ForEach-Object {
        $relative = $_.FullName.Substring($destination.Length).TrimStart('\', '/') -replace '\\', '/'
        [ordered]@{
            path = $relative
            length = $_.Length
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
$bundleManifest['files'] = @($payload)

$manifestJson = $bundleManifest | ConvertTo-Json -Depth 5
$manifestJson | Set-Content -LiteralPath (Join-Path $destination 'bundle-manifest.json') -Encoding utf8
$manifestJson | Set-Content -LiteralPath (Join-Path (Join-Path $destination 'desktop') 'bundle-manifest.json') -Encoding utf8

$hashes = Get-ChildItem -LiteralPath $destination -Recurse -File | Sort-Object FullName | ForEach-Object {
    $relative = $_.FullName.Substring($destination.Length).TrimStart('\', '/') -replace '\\', '/'
    "$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $relative"
}
$hashes | Set-Content -LiteralPath (Join-Path $destination 'SHA256SUMS') -Encoding utf8
Write-Host "Created deployment bundle: $destination"
Write-Warning 'This local bundle is unsigned. It is not a release attestation or an installed service.'
