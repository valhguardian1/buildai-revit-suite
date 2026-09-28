$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$probeHtml = Join-Path $root 'src\ViewerProbe.Shared\viewer-probe.html'
$integration = Join-Path $root 'src\BuildAI.Core\Issues\IssueIntegrationClient.cs'
$workflows = @(
    (Join-Path $root 'src\Plugin4.LinkComparatorAI\Issues\ComparatorIssueWorkflow.cs')
    (Join-Path $root 'src\Plugin5.ClashFormaIntegration\Issues\ClashIssueWorkflow.cs')
)

$probeText = [System.IO.File]::ReadAllText($probeHtml, [System.Text.Encoding]::UTF8)
$integrationText = [System.IO.File]::ReadAllText($integration, [System.Text.Encoding]::UTF8)

foreach ($required in @(
    'getExternalIdMapping(model)',
    'findDbIdCandidates(mapping, request)',
    "add(numericValue, key, 'externalId->dbId')",
    "add(numericKey, String(value), 'dbId->externalId')",
    'selectedLinkInstanceUid',
    'collectWorldBounds',
    'fragmentCount',
    'equally ranked geometric candidates'
)) {
    if (-not $probeText.Contains($required)) { throw "Viewer-first identity marker is missing: $required" }
}

if ($integrationText -notmatch '\["idType"\]\s*=\s*"lmv"' -or
    $integrationText -notmatch '\["id"\]\s*=\s*new JArray\(objectId\.Value\)' -or
    $integrationText -notmatch '\["isolated"\]\s*=\s*new JArray\(objectId\.Value\)' -or
    $integrationText -notmatch 'appearance\["ghostHidden"\]\s*=\s*true' -or
    $integrationText -notmatch 'AssertViewerHighlightState\(viewerState, new\[\] \{ objectId\.Value \}, false\)' -or
    $integrationText -notmatch 'ExternalId\s*=\s*string\.IsNullOrWhiteSpace\(externalId\)') {
    throw 'Issue payload objectSet/highlight/externalId serialization changed unexpectedly.'
}

foreach ($file in $workflows) {
    $text = [System.IO.File]::ReadAllText($file, [System.Text.Encoding]::UTF8)
    $requiredMarkers = @(
        'ViewerCoordinateProbe.ResolveBatchAsync',
        'SelectedLinkInstanceUid',
        'var resolvedExternalId = probe.ResolvedExternalId'
    )
    if ($file -like '*Plugin4.LinkComparatorAI*') {
        $requiredMarkers += @('RuntimeViewDbId', 'AuthoritativeIssueDbId', 'ReverseMatch', 'ArStIssuePayloadAdapter.Build')
    } else {
        $requiredMarkers += 'resolvedExternalId, probe.DbId, probe.ViewerState'
    }
    foreach ($required in $requiredMarkers) {
        if (-not $text.Contains($required)) { throw "Viewer-authoritative identity marker is missing in ${file}: $required" }
    }
    $forbiddenMarkers = @('ResolveObjectBoundsAsync', 'CompositeExternalId = objectMatch.ExternalId')
    if ($file -notlike '*Plugin4.LinkComparatorAI*') { $forbiddenMarkers += 'ResolveObjectAsync(' }
    foreach ($forbidden in $forbiddenMarkers) {
        if ($text.Contains($forbidden)) { throw "Obsolete Model Derivative identity dependency remains in ${file}: $forbidden" }
    }
}

$arStText = [System.IO.File]::ReadAllText($workflows[0], [System.Text.Encoding]::UTF8)
$clashText = [System.IO.File]::ReadAllText($workflows[1], [System.Text.Encoding]::UTF8)
if (-not $arStText.Contains('Viewer fragment geometry is the authoritative coordinate source for the surface-aware anchor.')) {
    throw 'AR-ST surface geometry authority marker is missing.'
}
if (-not $clashText.Contains('Viewer fragment world bounds are the authoritative coordinate source.')) {
    throw 'Accepted Clash world-bounds authority marker changed unexpectedly.'
}

Write-Host '[OK] Viewer selects a unique geometric externalId/dbId candidate using linked-model context.' -ForegroundColor Green
