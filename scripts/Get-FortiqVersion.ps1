<#
.SYNOPSIS
    Reads the Fortiq version from the one place it is decided.

.DESCRIPTION
    Directory.Build.props carries VersionPrefix and VersionSuffix, and the build gives them to every
    assembly. Anything outside the build that needs the version - the release archive's name, a
    bundle manifest, a release title - reads it from here rather than repeating it. A version
    repeated in two files is a version that will disagree with itself, which is how the archive came
    to say 0.1.0 while the application said 1.0.0.

.OUTPUTS
    An object with Prefix, Suffix, Full ("0.1.0-beta.1") and Archive ("0.1.0-beta.1"), the last being
    the form that is safe inside a file name.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$propsPath = Join-Path (Split-Path $PSScriptRoot -Parent) 'Directory.Build.props'
if (-not (Test-Path -LiteralPath $propsPath)) {
    throw "Directory.Build.props was not found at '$propsPath'; the version has no source."
}

[xml] $props = Get-Content -LiteralPath $propsPath -Raw

# Read element by element. Under StrictMode, reaching for a property on a set of PropertyGroups
# where only one carries it is an error rather than a null.
function Read-Property([string] $name) {
    foreach ($group in @($props.Project.PropertyGroup)) {
        $node = $group.SelectSingleNode($name)
        if ($node -and -not [string]::IsNullOrWhiteSpace($node.InnerText)) {
            return $node.InnerText.Trim()
        }
    }

    return $null
}

$prefix = Read-Property 'VersionPrefix'
$suffix = Read-Property 'VersionSuffix'

if ([string]::IsNullOrWhiteSpace($prefix)) {
    throw 'Directory.Build.props declares no VersionPrefix; refusing to guess a version.'
}

$full = if ([string]::IsNullOrWhiteSpace($suffix)) { $prefix } else { "$prefix-$suffix" }

[pscustomobject]@{
    Prefix  = $prefix
    Suffix  = $suffix
    Full    = $full
    Archive = $full
}
