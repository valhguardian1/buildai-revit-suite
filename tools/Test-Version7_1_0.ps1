$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

function Read-Utf8([string]$relativePath) {
    $path = Join-Path $root $relativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required 7.1.0 source is missing: $relativePath" }
    return Get-Content -LiteralPath $path -Raw
}
function Require([string]$text, [string]$needle, [string]$message) {
    if ($text.IndexOf($needle, [StringComparison]::Ordinal) -lt 0) { throw "$message Missing: $needle" }
}
function Reject([string]$text, [string]$needle, [string]$message) {
    if ($text.IndexOf($needle, [StringComparison]::Ordinal) -ge 0) { throw "$message Forbidden: $needle" }
}

Write-Host '==> Checking 7.1.0 dual Clash highlight'
$viewer = Read-Utf8 'src\ViewerProbe.Shared\viewer-probe.html'
$probe = Read-Utf8 'src\ViewerProbe.Shared\ViewerCoordinateProbe.cs'
$workflow = Read-Utf8 'src\Plugin5.ClashFormaIntegration\Issues\ClashIssueWorkflow.cs'
$client = Read-Utf8 'src\BuildAI.Core\Issues\IssueIntegrationClient.cs'
$log = Read-Utf8 'src\BuildAI.Core\Issues\IssueCreationFileLog.cs'

foreach ($marker in @(
    'const aggregateSelection = [{ model, ids: [resolved.dbId] }]',
    'request.highlightSecondary && secondaryTarget',
    'viewer.setAggregateSelection(aggregateSelection)',
    'viewer.getAggregateSelection()',
    'secondaryTarget.model === model',
    'viewerState.objectSet = viewerState.objectSet',
    'isolated: entry.id.slice()',
    'secondaryLoadedModelId:',
    'viewer.getState() omitted one or more selected Clash objects.')) {
    Require $viewer $marker 'Viewer dual-selection contract is incomplete.'
}
Reject $viewer 'viewer.select([resolved.dbId], model)' 'Primary-only Viewer selection remains.'
Reject $viewer 'id: [resolved.dbId]' 'Primary-only persisted objectSet remains.'

Require $probe '[JsonProperty("secondaryLoadedModelId")]' 'Secondary model identity is not returned to C#.'
Require $probe '["highlightSecondary"] = HighlightSecondary' 'Clash-only secondary highlight flag is not sent to Viewer.'
Require $workflow 'HighlightSecondary = true' 'Clash workflow does not request dual highlighting.'
Require $workflow 'SecondaryRequestedExternalId = useElementA ? row.ElementBUniqueId : row.ElementAUniqueId' 'Clash workflow does not identify the second element.'
Require $workflow 'probe.SecondaryDbId > 0 ? (int?)probe.SecondaryDbId : null' 'Clash workflow does not pass the second dbId.'
Require $workflow '!string.Equals(probe.LoadedModelId, probe.SecondaryLoadedModelId, StringComparison.Ordinal)' 'Cross-model Clash identity is not passed.'

foreach ($marker in @(
    'int? secondaryObjectId = null',
    'bool secondaryInDifferentModel = false',
    'new[] { objectId.Value, secondaryObjectId.Value }',
    'ConsumeExpectedIds(selected, expectedDbIds)',
    'ConsumeExpectedIds(isolated, expectedDbIds)',
    'requireMultipleObjectSets',
    'objectSetCount = entries.Count')) {
    Require $client $marker 'C# serialization boundary does not validate both Clash elements.'
}
Require $log 'objectSets=' 'Compact LLM log does not report aggregate Viewer state.'

Write-Host '[OK] Both Clash elements are selected and isolated with cross-model state preserved and validated before POST.' -ForegroundColor Green
