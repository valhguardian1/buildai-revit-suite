$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$viewerPath = Join-Path $root 'src\ViewerProbe.Shared\viewer-probe.html'
$viewer = [IO.File]::ReadAllText($viewerPath)

function Require([string]$value, [string]$message) {
    if ($viewer.IndexOf($value, [StringComparison]::Ordinal) -lt 0) { throw "$message Missing: $value" }
}
function Reject([string]$value, [string]$message) {
    if ($viewer.IndexOf($value, [StringComparison]::Ordinal) -ge 0) { throw "$message Forbidden: $value" }
}

# One vector owns both visibility of the surface-offset pin and the camera eye.
# If these calculations diverge, the target can be numerically correct while an
# opaque participant hides it and makes ACC appear to aim somewhere else.
Require 'const cameraDirection = exactIntersection' 'Clash camera direction is not derived from the exact intersection path.'
Require 'const outward = cameraDirection;' 'Pushpin exposure and camera placement do not share one direction.'
Require 'modelCentre, cameraDirection,' 'The shared direction is not passed into viewport construction.'

# Degenerate/vertical pair axes are common. A constant fallback recreates the
# reported symptom in which every Issue opens from one azimuth.
Require 'function adaptiveHorizontalDirection(' 'Geometry-adaptive camera fallback is missing.'
Require 'using the local bounds and clash location' 'Adaptive fallback diagnostics are missing.'
Reject 'using a fixed diagonal approach' 'A constant camera fallback remains active.'

# ACC must have one authoritative camera. A stale AutoCam destination from the
# published view can win after viewport restore and make all Issues look alike.
Require 'delete viewerState.autocam;' 'Competing AutoCam state is not removed.'
Require 'captured.target = viewport.target.slice();' 'Captured target is not normalized to the pushpin.'
Require 'captured.eye = viewport.eye.slice();' 'Captured eye is not normalized to the local camera.'
Require 'captured.up = viewport.up.slice();' 'Captured camera basis is not normalized.'
Require 'captured.orthographicHeight = viewport.orthographicHeight;' 'Orthographic local framing is not persisted.'
Require 'const savedCamera = JSON.parse(JSON.stringify(viewer.getState({ viewport: true })));' 'Snap camera is not saved before temporary navigation.'
Require 'viewer.restoreState({ viewport: savedCamera.viewport }, { viewport: true }, true)' 'Snap camera is not restored immediately.'
Require 'captureArstCamera(issueCamera, anchor.toArray(),' 'AR-ST final Issue camera is not applied through the stabilized lifecycle.'
Require 'compareArstCamera(state && state.viewport, viewport, pushpin' 'AR-ST serialized camera validation must remain intact.'
Require 'await captureIssueCamera(viewport, anchor.toArray())' 'Clash must capture its stabilized camera.'
Require 'CLASH_CAMERA_DISTANCE_FT = 0.70 / 0.3048' 'Clash camera distance is not constrained to 70 cm.'
Require 'clashLineOfSight(eye, anchor, allowed)' 'Camera line-of-sight check is missing.'
Require 'exact-solid-intersection-centroid/' 'Exact solid intersection anchor is not used.'
Require 'assertIssueCamera(state && state.viewport, actual, pushpin);' 'Serialized camera must match navigation and pushpin.'
Reject 'so the synthesized viewport was kept' 'Known failed camera capture must not be published.'

# AR-ST frame regression fixture: the pushpin remains viewer-local while the
# Issue viewport camera points are model/world coordinates after one offset.
$viewerLocal = @(-71.55056952606088, -106.42769268657383, -14.308445641726149)
$offset = @(188.17860412574373, -61.69368708752208, 6.6106133425367375)
$modelFrame = @(116.62803459968285, -168.1213797740959, -7.6978322991894115)
for ($i = 0; $i -lt 3; $i++) {
    if ([Math]::Abs(($viewerLocal[$i] + $offset[$i]) - $modelFrame[$i]) -gt 1e-9) { throw 'AR-ST model-frame diagnostic arithmetic regressed.' }
}
$modelDelta = [Math]::Sqrt((0..2 | ForEach-Object { [Math]::Pow($viewerLocal[$_] - $modelFrame[$_], 2) } | Measure-Object -Sum).Sum)
if ([Math]::Abs($modelDelta - 198.1439) -gt 0.01) { throw 'AR-ST globalOffset magnitude regression.' }
$payloadDelta = [Math]::Sqrt((0..2 | ForEach-Object { [Math]::Pow($viewerLocal[$_] + $offset[$_] - $modelFrame[$_], 2) } | Measure-Object -Sum).Sum)
if ($payloadDelta -gt 1e-9) { throw 'AR-ST absolute viewport target must equal viewer-local pushpin plus globalOffset.' }
$frameSource = [IO.File]::ReadAllText((Join-Path $root 'src\BuildAI.Core\Issues\PushpinFrame.cs'))
if ($frameSource -notmatch 'ValidateArStIssuePayload') { throw 'AR-ST absolute camera frame guard is missing.' }
if ($frameSource -notmatch 'expectedAbsoluteTarget') { throw 'AR-ST expected absolute target audit is missing.' }
$adapterSource = [IO.File]::ReadAllText((Join-Path $root 'src\BuildAI.Core\Issues\ArStIssuePayloadAdapter.cs'))
if ($adapterSource -notmatch 'Add\(localEye, globalOffset\)' -or $adapterSource -notmatch 'Add\(localTarget, globalOffset\)' -or $adapterSource -notmatch 'Add\(localPivot, globalOffset\)') { throw 'AR-ST camera offset must be applied once to all three camera points.' }
$workflowSource = [IO.File]::ReadAllText((Join-Path $root 'src\Plugin4.LinkComparatorAI\Issues\ComparatorIssueWorkflow.cs'))
if ($workflowSource -notmatch 'ArStIssuePayloadAdapter\.Build') { throw 'AR-ST does not use its isolated Issue payload adapter.' }

$node = Get-Command node -ErrorAction SilentlyContinue
$nodePath = if ($node) { $node.Source } else { Join-Path $root '.harness\tools\node\node.exe' }
if (-not (Test-Path -LiteralPath $nodePath)) { throw 'Node.js is required for the camera lifecycle regression tests (PATH or .harness/tools/node/node.exe).' }
& $nodePath (Join-Path $PSScriptRoot 'Test-CameraLifecycle.cjs')
if ($LASTEXITCODE -ne 0) { throw 'Camera lifecycle behavioral regression tests failed.' }

& $nodePath (Join-Path $PSScriptRoot 'Test-ClashNearCamera.cjs')
if ($LASTEXITCODE -ne 0) { throw 'Clash near-camera behavioral regression tests failed.' }

Write-Host '[OK] ACC Issue camera uses one adaptive local frame, targets the pushpin, and has no competing AutoCam state.' -ForegroundColor Green
exit 0
