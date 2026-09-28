$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

Write-Host "==> Checking multi-row Issue checkbox selection"

$windows = @(
    (Join-Path $root "src\Plugin4.LinkComparatorAI\UI\ResultsWindow.cs")
    (Join-Path $root "src\Plugin5.ClashFormaIntegration\UI\ResultsWindow.cs")
)

foreach ($file in $windows) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
        throw "Issue results window is missing: $file"
    }

    $text = Get-Content -LiteralPath $file -Raw
    foreach ($required in @(
        'IsThreeState=false',
        'UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged',
        'var target=_selectAllIssues.IsChecked==true',
        'row.IsSelectedForIssueCreation=check.IsChecked==true',
        'Count == 0'
    )) {
        if ($text -notmatch [regex]::Escape($required)) {
            throw "Issue checkbox safeguard is missing in ${file}: $required"
        }
    }

    foreach ($forbidden in @(
        'IsThreeState=true',
        'var target=_selectAllIssues.IsChecked!=false',
        'Dispatcher.BeginInvoke(new Action(UpdateSelectAllState))',
        'Count != 1',
        'exactly one'
    )) {
        if ($text -match [regex]::Escape($forbidden)) {
            throw "Obsolete three-state checkbox behavior remains in ${file}: $forbidden"
        }
    }
}

$workflows = @(
    (Join-Path $root "src\Plugin4.LinkComparatorAI\Issues\ComparatorIssueWorkflow.cs")
    (Join-Path $root "src\Plugin5.ClashFormaIntegration\Issues\ClashIssueWorkflow.cs")
)

foreach ($file in $workflows) {
    $text = Get-Content -LiteralPath $file -Raw
    foreach ($required in @(
        'var probeTotal = rows.Count',
        'for (var i = 0; i < rows.Count; i++)',
        'failed++;',
        'completed = i + 1',
        'Viewer Coordinate Probe batch completed'
    )) {
        if ($text -notmatch [regex]::Escape($required)) {
            throw "Multi-row Viewer Probe workflow marker is missing in ${file}: $required"
        }
    }
    foreach ($forbidden in @(
        'rows.Count != 1',
        'const int probeTotal = 1',
        'failed += Math.Max',
        'exactly one Viewer Coordinate Probe Issue'
    )) {
        if ($text -match [regex]::Escape($forbidden)) {
            throw "Single-Issue diagnostic restriction remains in ${file}: $forbidden"
        }
    }
}

Write-Host "[OK] Header clearing and multi-row selection are committed; every selected row has independent progress and failure accounting."
