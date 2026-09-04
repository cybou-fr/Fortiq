<#
.SYNOPSIS
    Turns failed tests in TRX files into GitHub Actions annotations.

.DESCRIPTION
    A failing job whose only annotation is "exit code 1" cannot be diagnosed without opening its log,
    and a log is not always reachable - a public repository does not serve its logs to an anonymous
    reader. Annotations are, so the names and messages of failed tests belong there.

    Prints nothing when every test passed, so it is safe to run unconditionally.
#>
[CmdletBinding()]
param(
    [string] $ResultsDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts/test-results')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path $ResultsDirectory)) {
    Write-Host "No test results under $ResultsDirectory"
    return
}

$failures = 0
foreach ($file in Get-ChildItem -Path $ResultsDirectory -Filter '*.trx' -Recurse) {
    [xml] $trx = Get-Content -LiteralPath $file.FullName -Raw
    $namespace = New-Object System.Xml.XmlNamespaceManager($trx.NameTable)
    $namespace.AddNamespace('t', 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010')

    foreach ($result in $trx.SelectNodes('//t:UnitTestResult[@outcome="Failed"]', $namespace)) {
        $failures++
        $name = $result.GetAttribute('testName')
        $message = $result.SelectSingleNode('t:Output/t:ErrorInfo/t:Message', $namespace).InnerText
        $stack = $result.SelectSingleNode('t:Output/t:ErrorInfo/t:StackTrace', $namespace)

        # One line per annotation: newlines end an annotation, and the point is to be readable
        # without the log this is standing in for.
        $flat = (($message + ' | ' + $(if ($stack) { $stack.InnerText } else { '' })) -replace '\r?\n', ' ' -replace '\s+', ' ').Trim()
        if ($flat.Length -gt 900) { $flat = $flat.Substring(0, 900) + '...' }

        Write-Host "::error title=$name::$flat"
    }
}

if ($failures -eq 0) {
    Write-Host 'No failed tests to report.'
}
else {
    Write-Host "Reported $failures failed test(s) as annotations."
}
