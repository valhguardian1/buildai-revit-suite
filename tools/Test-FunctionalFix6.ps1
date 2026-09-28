$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

function Assert-Contains([string]$Path, [string]$Needle) {
  $text = Get-Content -LiteralPath $Path -Raw
  if ($text.IndexOf($Needle, [System.StringComparison]::Ordinal) -lt 0) {
    throw "Expected text was not found in $Path`: $Needle"
  }
}

function Assert-NotContains([string]$Path, [string]$Needle) {
  $text = Get-Content -LiteralPath $Path -Raw
  if ($text.IndexOf($Needle, [System.StringComparison]::Ordinal) -ge 0) {
    throw "Forbidden text remains in $Path`: $Needle"
  }
}

$volumeCommand = Join-Path $root 'src\Plugin2.VolumeEstimator\Revit\Commands.cs'
$volumeResult = Join-Path $root 'src\Plugin2.VolumeEstimator\UI\ResultsWindow.cs'
$publicationClient = Join-Path $root 'src\Plugin2.VolumeEstimator\Publishing\BuildAiPublicationClient.cs'
$issueWorkflow = Join-Path $root 'src\Plugin4.LinkComparatorAI\Issues\ComparatorIssueWorkflow.cs'
$issueModels = Join-Path $root 'src\BuildAI.Core\Issues\IssueModels.cs'
$comparator = Join-Path $root 'src\Plugin4.LinkComparatorAI\Comparison\ComparatorEngine.cs'
$commands = Join-Path $root 'src\Plugin4.LinkComparatorAI\Revit\Commands.cs'

Assert-Contains $volumeCommand 'PublicationFlow.PublishAsync'
Assert-Contains $publicationClient '/api/revit_publish/'
Assert-Contains $publicationClient 'result.Url'
Assert-Contains $volumeResult 'Vol_PublishWait10'
Assert-Contains $issueModels '[JsonProperty("rootCauseId"'
Assert-Contains $issueWorkflow 'IssueLocationDetailsResolver.Resolve(row.Level, null, null, null)'
Assert-Contains $issueWorkflow 'LocationDetails = location.Value'
Assert-Contains $issueWorkflow 'Description = BuildDescription(row, operationId, generatedAtUtc)'
Assert-Contains $issueWorkflow 'IssueTextLocalizer.Details(x)'
Assert-Contains $issueWorkflow 'Check run ID:'
Assert-Contains $issueWorkflow 'Generated at UTC:'
Assert-NotContains $issueWorkflow 'Primary element:'
Assert-NotContains $issueWorkflow 'Secondary element:'
Assert-Contains $comparator 'if (includeRooms) CheckRoomHeights'
Assert-Contains $commands 'Run(data, ref message, true, false)'
Assert-Contains $commands 'Run(data, ref message, false, true)'

Write-Host '[OK] Fix6 publication, Issue fields, localization and room-height guards verified.' -ForegroundColor Green
