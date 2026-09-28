<#
.SYNOPSIS
Fails the build if any Cyrillic character reaches a user-facing string, or if the
Revit-localised Category.Name is read anywhere that feeds an Issue.

.DESCRIPTION
The product must render only in English or Hebrew, whichever the user configured.
Two paths let Russian in regardless of that setting:

  1. a literal typed into source or into a .resx;
  2. Element.Category.Name, which returns the name in the language Revit itself is
     running in - Russian on a Russian Revit - and was written straight into Issue
     titles and into the BuildAI payload.

The second is the one that actually shipped, which is why it is checked
mechanically rather than left to review.

Logging code is exempt: log files are diagnostic, never shown to a user, and the
redaction regexes there legitimately contain non-Latin ranges.
#>
[CmdletBinding()]
param([string]$Root = (Join-Path $PSScriptRoot '..'))

$ErrorActionPreference = 'Stop'
$failures = @()

$exempt = @('Logging\PluginLog.cs', 'Issues\IssueCreationFileLog.cs')
$sources = Get-ChildItem -Path (Join-Path $Root 'src') -Recurse -Include *.cs, *.resx, *.html, *.js |
    Where-Object { $file = $_.FullName; -not ($exempt | Where-Object { $file -like "*$_" }) }

foreach ($file in $sources) {
    $lineNumber = 0
    foreach ($line in Get-Content -LiteralPath $file.FullName -Encoding UTF8) {
        $lineNumber++
        # Comments are developer documentation and are not rendered or packaged
        # as product text. Enforce the rule on executable/resource content.
        $commentOnly = $line -match '^\s*(//|/\*|\*|\*/)' 
        $executableText = (($line -split '//', 2)[0] -replace '/\*.*?\*/', '')
        if (-not $commentOnly -and $executableText -cmatch '[\u0400-\u04FF]') {
            $failures += "Cyrillic text: $($file.FullName):$lineNumber"
        }
        if ($line -match 'Category\s*\??\.\s*Name' -and $line -notmatch '^\s*(//|\*|///)') {
            $failures += "Category.Name is locale-dependent, use CategoryNaming: $($file.FullName):$lineNumber"
        }
    }
}

# Every template key must exist in both languages, or a Hebrew user silently gets
# the English string back from the resource fallback and never reports it.
$en = ([xml](Get-Content -LiteralPath (Join-Path $Root 'src/BuildAI.Core/Localization/Strings.resx') -Raw)).root.data.name
$he = ([xml](Get-Content -LiteralPath (Join-Path $Root 'src/BuildAI.Core/Localization/Strings.he.resx') -Raw)).root.data.name
foreach ($key in $en) { if ($he -notcontains $key) { $failures += "Missing Hebrew translation: $key" } }
foreach ($key in $he) { if ($en -notcontains $key) { $failures += "Missing English translation: $key" } }

if ($failures.Count -gt 0) {
    Write-Host "LANGUAGE PURITY CHECK FAILED" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    exit 1
}

Write-Host "LANGUAGE PURITY CHECK PASSED" -ForegroundColor Green
Write-Host "  Files scanned: $($sources.Count)"
Write-Host "  Resource keys: $($en.Count) in both languages"
exit 0
