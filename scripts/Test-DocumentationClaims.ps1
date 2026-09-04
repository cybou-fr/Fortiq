<#
.SYNOPSIS
    Fails when documentation names a Fortiq type, project or file that does not exist.

.DESCRIPTION
    Documentation drifts towards intent. A specification written in the present indicative reads as
    a report on what is built, and nothing contradicts it when the code moves - which is how README
    came to claim an installer, a USN journal reader and a folder-picker service that were never
    written.

    This catches the mechanical half of that problem: an identifier in backticks that looks like a
    Fortiq type, or a repository path, with nothing behind it. It cannot catch a claim about
    behaviour - that ProvenRestore hashes files, say - because the name is real and only the
    sentence is wrong. Those stay a matter of review.

    Only backticked identifiers are considered, so prose is never guessed at. An identifier that is
    deliberately aspirational belongs in -KnownAbsent with a reason, which keeps the intent visible
    rather than silently tolerated.

.PARAMETER Path
    Documents to check. Defaults to README.md, SECURITY.md and docs/ when it is present - docs/ is
    excluded from version control, so CI checks the published files only.

.PARAMETER KnownAbsent
    Identifiers that documentation may name although no code defines them. Each needs a reason.
#>
[CmdletBinding()]
param(
    [string[]] $Path,
    [hashtable] $KnownAbsent = @{}
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent

# Named in specifications as design intent. Each is unbuilt on purpose; the entry says so, so that
# removing it from the documentation is a decision someone makes rather than something this script
# forces the day the wording changes.
$intent = @{
    'InstallWindow'          = 'Spec 21 / ADR-014: embedded installer, design intent, not implemented.'
    'ComponentHubView'       = 'Spec 17 section 6: component hub, design intent, not implemented.'
    'ReceiptsView'           = 'Spec 17 section 7: receipts inspector, design intent, not implemented.'
    'IPathPickerService'     = 'ADR-015: picker abstraction; the client calls StorageProvider directly.'
    'IStorageProviderAdapter' = 'ADR-015: picker abstraction, not implemented.'

    # Spec 23 section 7 is a component blueprint for a view-per-screen client. The desktop is built
    # in code with two windows, so none of these types exist. They are the target shape, not a
    # description of the repository; Spec 23 says so under its implementation status.
    'HomeView'                 = 'Spec 23 blueprint: no view classes exist; the client is code-built.'
    'BackupsView'              = 'Spec 23 blueprint: no view classes exist.'
    'SettingsView'             = 'Spec 23 blueprint: Settings is a placeholder in MainWindow.'
    'ProtectWizardView'        = 'Spec 23 blueprint: the wizard is ProtectRepositoryWindow.'
    'RecoveryProofView'        = 'Spec 23 blueprint: recovery lives in MainWindow.'
    'RecoveryKitView'          = 'Spec 23 blueprint, not implemented.'
    'MainViewModel'            = 'Spec 23 blueprint: MainWindow drives RepositoriesViewModel directly.'
    'DashboardViewModel'       = 'Spec 23 blueprint, not implemented.'
    'AppNavigationItemViewModel' = 'Spec 23 blueprint: navigation is built inline in MainWindow.'
    'LastProvenRestore'        = 'Spec 23 blueprint property; the fact is RepositoryFacts.LastProvenRestoreAt.'

    # Installer and control plane: whole subsystems specified and not built.
    'IInstallationInspector'   = 'Spec 21 / ADR-014: installer, design intent, not implemented.'
    'UpdatePolicy'             = 'ADR-010: metadata-only control plane, design intent.'
    'Fortiq.Backup.Desktop'    = 'ADR-015: AppUserModelID for taskbar grouping, design intent, not registered.'
}
foreach ($entry in $KnownAbsent.GetEnumerator()) { $intent[$entry.Key] = $entry.Value }

if (-not $Path) {
    $Path = @('README.md', 'SECURITY.md') |
        ForEach-Object { Join-Path $root $_ } |
        Where-Object { Test-Path $_ }

    $docs = Join-Path $root 'docs'
    if (Test-Path $docs) {
        $Path += (Get-ChildItem $docs -Recurse -Filter *.md).FullName
    }
}

# Every identifier the code actually defines, plus the project and script names on disk.
$defined = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($file in Get-ChildItem (Join-Path $root 'src'), (Join-Path $root 'tests') -Recurse -Filter *.cs |
        Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' }) {

    foreach ($match in [regex]::Matches(
            (Get-Content $file.FullName -Raw),
            '\b(?:class|interface|record|struct|enum)\s+(\w+)')) {
        [void] $defined.Add($match.Groups[1].Value)
    }
}

foreach ($directory in 'src', 'tests') {
    Get-ChildItem (Join-Path $root $directory) -Directory | ForEach-Object { [void] $defined.Add($_.Name) }
}

# Deliberately narrow. A first attempt matched anything PascalCase and produced 139 hits on a healthy
# repository - enum members, CLI verbs, JSON fields, Avalonia controls, environment variables. A check
# that noisy gets switched off, and then it protects nothing.
#
# What is matched instead is the shape of the claim that actually went wrong: a named Fortiq
# component. Those names end in a role suffix, and documentation invents them - InstallWindow,
# ComponentHubView, ReceiptsView - long before anyone writes them. Everything else (behaviour, verbs,
# field names, framework types) is left to review, which is where it belongs.
$roles = 'View|ViewModel|Window|Service|Adapter|Provider|Store|Publisher|Runner|Verifier|' +
    'Inspector|Repository|Broker|Registry|Assessor|Encoder|Parser|Codec|Envelope|Kit|Policy'
$looksLikeIdentifier = "^(?:I?[A-Z][A-Za-z0-9]*(?:$roles)|Fortiq(?:\.[A-Za-z0-9]+)+)$"

$problems = @()
foreach ($document in $Path) {
    $text = Get-Content $document -Raw
    $relative = ($document -replace [regex]::Escape($root + [IO.Path]::DirectorySeparatorChar), '').Replace('\', '/')

    foreach ($match in [regex]::Matches($text, '`([^`\r\n]+)`')) {
        $token = $match.Groups[1].Value.Trim()
        if ($token -cnotmatch $looksLikeIdentifier) { continue }
        if ($defined.Contains($token)) { continue }
        if ($intent.ContainsKey($token)) { continue }

        # `Fortiq.Desktop.csproj`, `Fortiq.Recover.exe` and the AppUserModelID `Fortiq.Backup.Desktop`
        # are files and artifact identifiers, not types. Only a bare project-shaped name is checked,
        # and only when nothing on disk answers to it.
        if ($token -clike 'Fortiq.*') {
            if ($token -cmatch '\.(csproj|exe|dll|sln|json|ps1|md)$') { continue }
            if (Test-Path (Join-Path $root "src/$token")) { continue }
            if (Test-Path (Join-Path $root "tests/$token")) { continue }
        }

        $line = ($text.Substring(0, $match.Index) -split "`n").Count
        $problems += [pscustomobject]@{ File = $relative; Line = $line; Token = $token }
    }
}

# One line per identifier, not per mention: a name used in eight places is one problem.
$unique = @($problems | Group-Object Token | Sort-Object Name)
foreach ($group in $unique) {
    $first = $group.Group[0]
    $where = if ($group.Count -eq 1) { '' } else { " (and $($group.Count - 1) more)" }
    $message = "Documentation names ``$($first.Token)``, which no code defines$where. " +
        'Correct the document, or add the name to -KnownAbsent with the reason it is intentional.'

    if ($env:GITHUB_ACTIONS) {
        Write-Host "::error file=$($first.File),line=$($first.Line)::$message"
    }

    Write-Host "$($first.File):$($first.Line): $message"
}

if ($unique.Count -gt 0) {
    throw "$($unique.Count) documented identifier(s) do not exist in the code."
}

Write-Host "Checked $($Path.Count) document(s); every named Fortiq identifier exists."
