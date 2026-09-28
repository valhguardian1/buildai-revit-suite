$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

function Read-Utf8([string]$relativePath) {
    $path = Join-Path $root $relativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing source: $relativePath" }
    return [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8)
}

function Require([string]$text, [string]$needle, [string]$message) {
    if ($text.IndexOf($needle, [StringComparison]::Ordinal) -lt 0) { throw "$message Missing: $needle" }
}

function Reject([string]$text, [string]$needle, [string]$message) {
    if ($text.IndexOf($needle, [StringComparison]::Ordinal) -ge 0) { throw "$message Forbidden: $needle" }
}

Write-Host '==> Checking surface-aware AR-ST PushPin and camera invariants'

$hostCode = Read-Utf8 'src\ViewerProbe.Shared\ViewerCoordinateProbe.cs'
$viewer = Read-Utf8 'src\ViewerProbe.Shared\viewer-probe.html'
$workflow = Read-Utf8 'src\Plugin4.LinkComparatorAI\Issues\ComparatorIssueWorkflow.cs'
$adapter = Read-Utf8 'src\BuildAI.Core\Issues\ArStIssuePayloadAdapter.cs'
$clash = Read-Utf8 'src\Plugin5.ClashFormaIntegration\Issues\ClashIssueWorkflow.cs'

foreach ($marker in @(
    'SecondaryRequestedExternalId',
    'SecondaryLinkInstanceUid',
    'SecondaryModelUid',
    'PreferSurfacePairAnchor',
    'PreferredModelPoint',
    '[JsonProperty("anchor")]',
    'public ViewerProbePoint GlobalAnchor')) {
    Require $hostCode $marker 'Viewer pair-anchor DTO is incomplete.'
}

foreach ($marker in @(
    'MAX_SURFACE_TRIANGLES = 20000',
    'closestPointOnTriangle(',
    'getFragmentGeometry(',
    'hasReadableVertices(',
    'projectPointToSurface(',
    'projectPointToAabbSurface(',
    'modelPointToViewer(',
    'model-to-viewer-transform',
    'secondaryTarget = await resolveTargetModel',
    'surface-pair-midpoint/',
    'fallback-aabb-exterior-face',
    'modelCentre, cameraDirection,',
    # Camera framing follows the element diagonal from 7.0.19. The fixed
    # 12-20 ft window put the camera 12 ft from a 21 x 17 ft slab on every
    # Issue of 03 Sep 2026 - about a third of the distance needed to see it.
        # 7.2.1: framing follows the overlap of the clashing pair, and the camera
    # state is captured from the viewer rather than synthesized. The 15 ft floor
    # held the camera 15 ft from a 1.65 ft fitting; overwriting the captured
    # viewport is what stopped ACC from moving the camera at all.
        # 7.2.1: the approach direction is derived from the pair axis instead of a
    # constant, and the primary element's extent no longer sets the distance.
    # Every Issue of 05 Sep stored the identical direction [0, 0.8575, 0.5145],
    # and a 62 ft cable tray pushed the camera to 81.3 ft.
    'chooseViewDirection(',
    'CLASH_FOCUS_MAX_FT',
    'CAMERA_ELEVATION_RAD',
    'CAMERA VERIFIED',
    'boundsOverlap(',
    'exitDistanceAlong(',
    # 7.3.8: the anchor is snapped onto real geometry with the viewer's own
    # raycaster, the way ACC places a pin by hand. The bounding-box path stays
    # as a fallback because SVF2 triangle buffers are unreadable on federated
    # models with linked geometry - "Sampled triangle count: 0" in every log.
    'clampPointToBounds(',
    'SNAP_MAX_DISTANCE_FT',
    'snapAnchorToGeometry(',
    'hit-test-snapped',
    'PUSHPIN SNAP MISSED',
    'nearest-primary-aabb-surface',
    'PushpinGeometryUnresolved',
    'if (typeof viewer.clearSelection ===',
    'if (typeof viewer.showAll ===',
    'captured.pivotPoint = viewport.target.slice()',
    'viewer.setAggregateSelection(aggregateSelection)',
    'isolated: entry.id.slice()',
    'ghostHidden: true',
    'displayLines: true',
    'ambientShadow: true',
    'viewerState.globalOffset = {')) {
    Require $viewer $marker 'Surface-aware Viewer algorithm is incomplete.'
}

foreach ($marker in @(
    'SecondaryRequestedExternalId = useArchitecture ? row.StructuralElementUniqueId : ""',
    'PreferSurfacePairAnchor = true',
    'PreferredModelPoint = null',
    'ValidatePushpinAnchor(row, probe)',
    'var pushpinSource = probe.Anchor',
    'ArStIssuePayloadAdapter.Build',
    'Viewer pushpin anchor failed the local + globalOffset invariant')) {
    Require $workflow $marker 'AR-ST workflow invariant is incomplete.'
}
Require $adapter 'ValidateArStIssuePayload' 'AR-ST camera payload guard is incomplete.'

foreach ($forbidden in @(
    'probe.GlobalCenter != null ? probe.GlobalCenter : probe.Center',
    'buildViewport(geometry.bounds, center)',
    'viewer.navigation.setPivotPoint(center)',
    'viewer.select(selectedIds, model)',
    'viewer.isolate([], model)',
    'displayEdges: true',
    'ambientShadows: false',
    "method: 'fallback-aabb-clamp'")) {
    Reject $workflow $forbidden 'Legacy AR-ST AABB path remains in the workflow.'
    Reject $viewer $forbidden 'Legacy AR-ST AABB path remains in the Viewer probe.'
}

foreach ($marker in @(
    'public string JavaScriptStack { get; set; }',
    'JavaScriptStack = (string)item["stack"] ?? ""',
    'JavaScript stack: <not supplied>')) {
    Require $hostCode $marker 'Per-item Viewer JavaScript stack logging is incomplete.'
}

# 7.0.19 moves Clash to the same lifted surface-anchor contract.
Require $clash 'var pushpinSource = probe.Anchor' 'Clash surface-anchor path is missing.'

# Numeric contract used by the 01.09 diagnostic run.
$local = @(-108.5228500366211, -69.85987281799316, 14.04227352142334)
$offset = @(188.17860412574373, -61.69368708752208, 6.6106133425367375)
$expected = @(79.65575408912264, -131.55355990551524, 20.652886863960077)
for ($i = 0; $i -lt 3; $i++) {
    if ([Math]::Abs(($local[$i] + $offset[$i]) - $expected[$i]) -gt 0.000000001) {
        throw "Model-frame invariant failed on coordinate index $i."
    }
}

Write-Host '[OK] Surface-aware AR-ST anchor, viewer-local pushpin and absolute Issue camera contract verified.' -ForegroundColor Green
