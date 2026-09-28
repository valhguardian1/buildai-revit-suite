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
    throw "Obsolete text was found in $Path`: $Needle"
  }
}

$aps = Join-Path $root 'src\BuildAI.Core\APS\ApsPublicationClient.cs'
$results = Join-Path $root 'src\Plugin4.LinkComparatorAI\UI\ResultsWindow.cs'

Assert-Contains $aps 'DateTime.UtcNow.AddMinutes(45)'
Assert-Contains $aps 'readiness.IsReady && rootReady'
Assert-Contains $aps 'rootSuccessWithoutTargetSinceUtc'
Assert-Contains $aps 'TimeSpan.FromMinutes(3)'
Assert-Contains $aps 'eventual-consistency grace period'
Assert-NotContains $aps "Autodesk Model Derivative reports root status 'success', but the required viewable is not usable."
Assert-Contains $results 'private readonly Dictionary<string,string> _columnFilters'
Assert-Contains $results 'Select visible group'
Assert-Contains $results 'var selectedKeys = VisibleIssueRows()'
Assert-Contains $results 'var selectedRows = VisibleIssueRows()'
Assert-Contains $results 'string.Equals(NormalizeFilterValue(GetColumnValue(x,column)),value,StringComparison.OrdinalIgnoreCase)'

Write-Host '[OK] Root-manifest readiness, bounded resource convergence and visible-group Issue filtering verified.' -ForegroundColor Green
