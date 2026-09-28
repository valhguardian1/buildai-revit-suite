$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

function Read-Utf8([string]$relativePath) {
    $path = Join-Path $root $relativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required source is missing: $relativePath"
    }
    return Get-Content -LiteralPath $path -Raw
}

function Require([string]$text, [string]$needle, [string]$message) {
    if ($text.IndexOf($needle, [StringComparison]::Ordinal) -lt 0) {
        throw "$message Missing: $needle"
    }
}

function Reject([string]$text, [string]$needle, [string]$message) {
    if ($text.IndexOf($needle, [StringComparison]::Ordinal) -ge 0) {
        throw "$message Forbidden: $needle"
    }
}

$viewer = Read-Utf8 'src\ViewerProbe.Shared\viewer-probe.html'
$issues = Read-Utf8 'src\BuildAI.Core\Issues\IssueIntegrationClient.cs'
$fileLog = Read-Utf8 'src\BuildAI.Core\Issues\IssueCreationFileLog.cs'
$arSt = Read-Utf8 'src\Plugin4.LinkComparatorAI\Issues\ComparatorIssueWorkflow.cs'
$frame = Read-Utf8 'src\BuildAI.Core\Issues\PushpinFrame.cs'

foreach ($marker in @(
    'viewer.setAggregateSelection(aggregateSelection)',
    'viewer.getSelection(model)',
    'isolated: entry.id.slice()',
    'ghostHidden: true',
    'ambientShadow: true')) {
    Require $viewer $marker 'Viewer highlight capture is incomplete.'
}

foreach ($marker in @(
    '["isolated"] = new JArray(objectId.Value)',
    'appearance["ghostHidden"] = true',
    'AssertViewerHighlightState(viewerState, new[] { objectId.Value }, false)',
    'VIEWERSTATE HIGHLIGHT INVALID')) {
    Require $issues $marker 'Final Issue highlight serialization is incomplete.'
}

foreach ($marker in @(
    'BuildAI-LLM-Diagnostic/1',
    'CompactForLlm(message ?? string.Empty)',
    'CompactIssueRequest',
    'CompactIssueResponse',
    'CompactViewerResult',
    'CompactViewerCandidates',
    'Body summary:',
    'LLM_LOG_TRUNCATED')) {
    Require $fileLog $marker 'Compact LLM diagnostic log is incomplete.'
}

# The appearance patch must not restore either rejected coordinate conversion.
foreach ($marker in @(
    'var pushpinSource = probe.Anchor',
    'pushpinX = pushpinSource.X',
    'pushpinY = pushpinSource.Y',
    'pushpinZ = pushpinSource.Z')) {
    Require $arSt $marker 'Viewer-local PushPin transport changed unexpectedly.'
}
Reject $arSt 'pushpinSource.X * pushpinUnitScaleToMeters' 'Metre conversion returned to the active PushPin path.'
Reject $frame 'NormalizeViewerState(' 'Legacy Viewer-state coordinate mutation returned.'
Reject $viewer 'viewer.fitToView([resolved.dbId], model)' 'Animated whole-element camera regression returned.'
Reject $viewer 'viewer.isolate([resolved.dbId], model)' 'Batch Viewer isolation leak returned.'

Write-Host '[OK] Issue highlight and compact LLM log contracts verified.' -ForegroundColor Green
