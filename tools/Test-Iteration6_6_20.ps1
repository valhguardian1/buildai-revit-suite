$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

function Assert-Contains([string]$Path, [string]$Needle) {
  $text = Get-Content -LiteralPath $Path -Raw
  if ($text.IndexOf($Needle, [System.StringComparison]::Ordinal) -lt 0) {
    throw "Expected text was not found in $Path`: $Needle"
  }
}

$fileLog = Join-Path $root 'src\BuildAI.Core\Issues\IssueCreationFileLog.cs'
$volumeCommand = Join-Path $root 'src\Plugin2.VolumeEstimator\Revit\Commands.cs'
$autoRecalculate = Join-Path $root 'src\Plugin2.VolumeEstimator\Revit\RecalcExternalEventHandler.cs'
$publication = Join-Path $root 'src\Plugin2.VolumeEstimator\Publishing\PublicationFlow.cs'
$arStResults = Join-Path $root 'src\Plugin4.LinkComparatorAI\UI\ResultsWindow.cs'

Assert-Contains $fileLog 'BeginOperationSession(string operation, string source)'
Assert-Contains $fileLog 'public static void WriteTo(string path, string message)'
Assert-Contains $fileLog 'CompactForLlm(message ?? string.Empty)'
Assert-Contains $fileLog 'CompactIssueRequest'
Assert-Contains $fileLog 'CompactViewerResult'
Assert-Contains $fileLog 'Body summary:'
Assert-Contains $volumeCommand 'BeginOperationSession("Recalculate", "Volumes")'
Assert-Contains $volumeCommand 'ResultsPane.SetStatus("Diagnostic log: " + recalculateLogPath)'
Assert-Contains $autoRecalculate 'BeginOperationSession("Recalculate", "Volumes-AutoSync")'
Assert-Contains $publication 'VIEWER PUBLICATION COMPLETED'
Assert-Contains $publication 'WriteFatalTo(operationLogPath, "VIEWER PUBLICATION FAILED"'
Assert-Contains $arStResults 'var issueLogPath = IssueCreationFileLog.BeginSession("AR-ST")'
Assert-Contains $arStResults 'RECALCULATE FAILED'
Assert-Contains $arStResults 'RequestRecalculate(recalculateLog)'

Write-Host '[OK] 6.6.20 Recalculate diagnostic logging verified.' -ForegroundColor Green
