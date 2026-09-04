[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [switch] $NoBuild
)

$ErrorActionPreference = 'Stop'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'The VSS integration test requires an elevated Administrator PowerShell session.'
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repositoryRoot 'tests/Fortiq.Recovery.IntegrationTests/Fortiq.Recovery.IntegrationTests.csproj'
$arguments = @(
    'test'
    $project
    '--configuration'
    $Configuration
    '--filter'
    'PrivilegeMode=ElevatedVss'
)

if ($NoBuild) {
    $arguments += '--no-build'
}

& dotnet @arguments
if ($LASTEXITCODE -ne 0) {
    throw "The elevated VSS integration lane failed with exit code $LASTEXITCODE."
}
