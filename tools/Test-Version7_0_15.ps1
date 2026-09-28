$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

function Read-Utf8([string]$relativePath) {
    $path = Join-Path $root $relativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required 7.1.0 source is missing: $relativePath"
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

Write-Host '==> Checking 7.1.0 visible PushPins, Viewer batch recovery and one-command build'

$probe = Read-Utf8 'src\ViewerProbe.Shared\viewer-probe.html'
$probeHost = Read-Utf8 'src\ViewerProbe.Shared\ViewerCoordinateProbe.cs'
$issuesClient = Read-Utf8 'src\BuildAI.Core\Issues\IssueIntegrationClient.cs'
$arStWorkflow = Read-Utf8 'src\Plugin4.LinkComparatorAI\Issues\ComparatorIssueWorkflow.cs'
$arSt = Read-Utf8 'src\Plugin4.LinkComparatorAI\Revit\ComparatorActionHandler.cs'
$clash = Read-Utf8 'src\Plugin5.ClashFormaIntegration\Revit\ModelActionHandler.cs'
$props = Read-Utf8 'Directory.Build.props'
$wix = Read-Utf8 'installer\BuildAI.RevitSuite.wxs'
$inno = Read-Utf8 'installer\BuildAI.iss'
$coreProject = Read-Utf8 'src\BuildAI.Core\BuildAI.Core.csproj'
$buildInfo = Read-Utf8 'src\BuildAI.Core\BuildInfo.cs'
$buildScript = Read-Utf8 'build.ps1'
$prerequisites = Read-Utf8 'tools\Test-BuildPrerequisites.ps1'

Require $arSt 'CoordinationViewVisibility.EnsureFull(doc, coordination)' 'AR-ST coordination visibility restoration regressed.'
Require $clash 'MepComparisonViewComposer.Compose(doc, coordination, PluginContext.Report)' 'Scoped MEP coordination composition regressed.'
Require $arSt 'CoordinationViewVisibility.EnsureFull(doc, arSt)' 'AR-ST visibility must be fully restored by the AR-ST workflow before deliberate filtering.'
Require $arSt 'ResetView(arSt)' 'AR-ST section-box reset regressed in the AR-ST workflow.'
Require $clash 'AR-ST VIEW PRESERVED' 'Clash workflow no longer reports the AR-ST isolation invariant.'
foreach ($forbidden in @(
    'EnsureNamed3D(doc,type,BuildAiViewNames.ArSt)',
    'ResetView(arSt)',
    'CoordinationViewVisibility.EnsureFull(doc, arSt)',
    'SetRelevantLinkVisibility(doc, arSt',
    'HideMepFromArSt(doc, arSt')) {
    Reject $clash $forbidden 'Clash workflow must not mutate the AR-ST view.'
}

foreach ($marker in @(
    'MAX_SURFACE_TRIANGLES = 20000',
    'surface-pair-midpoint/',
    'projectPointToSurface(',
    'projectPointToAabbSurface(',
    'fallback-aabb-exterior-face',
    'hasReadableVertices(',
    'modelPointToViewer(',
    'localFocusRadius',
    # Camera framing follows the element diagonal from 7.0.19. The fixed
    # 12-20 ft window put the camera 12 ft from a 21 x 17 ft slab on every
    # Issue of 03 Sep 2026 - about a third of the distance needed to see it.
        # 7.3: framing follows the overlap of the clashing pair, and the camera
    # state is captured from the viewer rather than synthesized. The 15 ft floor
    # held the camera 15 ft from a 1.65 ft fitting; overwriting the captured
    # viewport is what stopped ACC from moving the camera at all.
        # 7.3: the approach direction is derived from the pair axis instead of a
    # constant, and the primary element's extent no longer sets the distance.
    # Every Issue of 05 Sep stored the identical direction [0, 0.8575, 0.5145],
    # and a 62 ft cable tray pushed the camera to 81.3 ft.
    'chooseViewDirection(',
    'CLASH_FOCUS_MAX_FT',
    'CAMERA_ELEVATION_RAD',
    'CAMERA CAPTURED FROM VIEWER',
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
    'viewer.setAggregateSelection(aggregateSelection)',
    'isolated: entry.id.slice()',
    'ghostHidden: true',
    'displayLines: true',
    'ambientShadow: true',
    'viewerState.globalOffset = {',
    'x: modelGlobalOffset.x',
    'y: modelGlobalOffset.y',
    'z: modelGlobalOffset.z',
    'stateGlobalOffset: viewerState.globalOffset || null')) {
    Require $probe $marker 'Persisted PushPin state is incomplete.'
}

foreach ($forbidden in @(
    'viewer.fitToView([resolved.dbId], model)',
    'viewer.isolate([resolved.dbId], model)',
    'viewer.isolate([], model)',
    'buildViewport(geometry.bounds, center)',
    'displayEdges: true',
    'ambientShadows: false')) {
    Reject $probe $forbidden 'Whole-element camera/isolation regression detected.'
}

foreach ($marker in @(
    'public string JavaScriptStack { get; set; }',
    'JavaScriptStack = (string)item["stack"] ?? ""',
    'JavaScript stack: <not supplied>')) {
    Require $probeHost $marker 'Per-item Viewer JavaScript diagnostics are incomplete.'
}

foreach ($marker in @(
    'if (capturedViewerState == null)',
    'A Viewer-captured state is required to create a PushPin document.',
    'var viewerState = (JObject)capturedViewerState.DeepClone();',
    '["isolated"] = new JArray(objectId.Value)',
    'appearance["ghostHidden"] = true',
    'AssertViewerHighlightState(viewerState, new[] { objectId.Value }, false)')) {
    Require $issuesClient $marker 'Captured Viewer-state guard is incomplete.'
}
foreach ($forbidden in @(
    'var fallbackViewerState = new JObject',
    '? fallbackViewerState')) {
    Reject $issuesClient $forbidden 'Fabricated fallback camera remains.'
}

foreach ($marker in @(
    'SecondaryRequestedExternalId',
    'PreferSurfacePairAnchor',
    'PreferredModelPoint',
    'public ViewerProbePoint GlobalAnchor')) {
    Require $probeHost $marker 'Pair-aware Viewer host contract is incomplete.'
}

foreach ($marker in @(
    'SecondaryRequestedExternalId = useArchitecture ? row.StructuralElementUniqueId : ""',
    'PreferSurfacePairAnchor = true',
    'PreferredModelPoint = null',
    'ValidatePushpinAnchor(row, probe)',
    'var pushpinSource = probe.Anchor')) {
    Require $arStWorkflow $marker 'AR-ST surface-aware pushpin contract is incomplete.'
}

foreach ($forbidden in @(
    'probe.GlobalCenter != null ? probe.GlobalCenter : probe.Center',
    'BuildProbeRequest(row, pushpinContext, false)')) {
    Reject $arStWorkflow $forbidden 'Legacy AR-ST AABB pushpin path remains.'
}

$versionMatch = [regex]::Match($props, '<Version>(\d+\.\d+(?:\.\d+)?)</Version>')
if (-not $versionMatch.Success) { throw 'Directory.Build.props has no semantic product version.' }
$expectedVersion = $versionMatch.Groups[1].Value
Require $coreProject "<Version>$expectedVersion</Version>" 'Core project version mismatch.'
Require $wix "Version=`"$expectedVersion`"" 'MSI version mismatch.'
Require $buildInfo "public const string Version = `"$expectedVersion`";" 'Runtime version mismatch.'
Require $wix 'assets\BuildAI.ico' 'WiX must package the supplied product icon.'
Require $inno 'assets\BuildAI_Setup.ico' 'The optional Inno script must use the supplied setup icon.'
foreach ($icon in @('installer\assets\BuildAI.ico', 'installer\assets\BuildAI_Setup.ico')) {
    if (-not (Test-Path -LiteralPath (Join-Path $root $icon) -PathType Leaf)) {
        throw "Installer icon is missing: $icon"
    }
}

foreach ($marker in @(
    "Invoke-ValidationScript 'tools\Test-BuildPrerequisites.ps1'",
    "Invoke-ValidationScript 'tools\Test-AecModelDataReadiness.ps1'",
    "Invoke-ValidationScript 'tools\Test-LargeModelIssueResolution.ps1'",
    "Invoke-ValidationScript 'tools\Test-IssueCheckboxSelection.ps1'",
    "Invoke-ValidationScript 'tools\Test-Plugin4RibbonStartup.ps1'",
    "Invoke-ValidationScript 'tools\Test-ArStLinkRefresh.ps1'",
    "Invoke-ValidationScript 'tools\Test-SurfaceAwareArStPushpins.ps1'",
    "Invoke-ValidationScript 'tools\Test-Version7_0_15.ps1'",
    '$validationExitCode = $LASTEXITCODE',
    'Unsupported Revit version(s):',
    'The release MSI must contain every supported Revit payload.',
    "Invoke-ValidationScript 'tools\Test-Version7_0_21.ps1'",
    "Invoke-ValidationScript 'tools\Test-Version7_1_0.ps1'",
    "BuildAI_RevitSuite_${expectedVersion}_Setup.msi")) {
    Require $buildScript $marker 'One-command build invariant is incomplete.'
}

foreach ($marker in @(
    'PowerShell 7 or later is required',
    "Test-RequiredCommand -Name 'dotnet'",
    "Test-RequiredCommand -Name 'wix'",
    'IExpress is required',
    '.NET Framework 4.8 Developer Pack',
    'WiX 5.x is required')) {
    Require $prerequisites $marker 'Early prerequisite validation is incomplete.'
}

Write-Host '[OK] 7.1.0 preserves the Autodesk PushPin coordinate-frame contract, isolates Clash publication from AR-ST and validates per-item Viewer recovery plus the one-command build pipeline.' -ForegroundColor Green
