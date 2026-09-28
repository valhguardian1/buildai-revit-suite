$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

function Read-Utf8([string]$relative) {
  $path = Join-Path $root $relative
  if (-not (Test-Path -LiteralPath $path)) { throw "Anchor probe resource is missing: $relative" }
  return [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8)
}
function Assert-Contains([string]$text, [string]$fragment, [string]$message) {
  if ($text.IndexOf($fragment, [StringComparison]::Ordinal) -lt 0) { throw $message }
}
function Assert-NotContains([string]$text, [string]$fragment, [string]$message) {
  if ($text.IndexOf($fragment, [StringComparison]::Ordinal) -ge 0) { throw $message }
}

$probe = Read-Utf8 'src\ViewerProbe.Shared\buildai-anchor-probe.js'
$html  = Read-Utf8 'src\ViewerProbe.Shared\viewer-probe.html'
$p4    = Read-Utf8 'src\Plugin4.LinkComparatorAI\Plugin4.LinkComparatorAI.csproj'
$p5    = Read-Utf8 'src\Plugin5.ClashFormaIntegration\Plugin5.ClashFormaIntegration.csproj'

# Position and camera must be produced in BOTH frames, from the same anchor.
# Returning the camera in one frame while the position can be written in the
# other is the exact defect that made 7.0.12 place pins and camera about 57 m
# apart, and it is invisible until somebody opens the Issue in ACC.
foreach ($fragment in @('positionViewer', 'positionModel', 'cameraViewer', 'cameraModel', 'deriveCameras')) {
  Assert-Contains $probe $fragment "Anchor probe must return both coordinate frames: $fragment"
}
Assert-NotContains $probe 'res.camera =' 'A single-frame camera reintroduces the 7.0.12 position/camera split.'

# hitTest reads the current camera matrices. setView and isolate only take
# effect after a redraw, so a synchronous shot lands on the previous camera,
# misses, and silently degrades every anchor to a bounding-box centre.
foreach ($fragment in @('viewer.impl.invalidate(true, true, true)', 'nextFrame()', 'function anchorByHitTest')) {
  Assert-Contains $probe $fragment "Anchor probe must let the viewer redraw before hitTest: $fragment"
}
Assert-Contains $probe 'viewer.impl.canvas' 'hitTest must aim at the canvas centre, not the container centre.'

# The runtime calibration is the whole point of this probe: the frame is
# measured, never assumed.
foreach ($fragment in @('function detectFrame', 'votes.ViewerLocal', 'votes.Model', 'confidence')) {
  Assert-Contains $probe $fragment "Runtime frame calibration is missing: $fragment"
}

# dbId is assigned per translation and does not survive republication, so a
# reference pin created against an older version points at a different element.
Assert-Contains $probe 'resolvedByExternalId' 'Calibration must prefer externalId over the per-translation dbId.'

# Box3.isEmpty/empty differ between the THREE revisions Autodesk ships.
Assert-NotContains $probe '.isEmpty()' 'Box3.isEmpty() is not available in every viewer build; check finiteness instead.'
Assert-Contains $probe 'function boxIsUsable' 'Bounds validation must not depend on a THREE revision.'

# Requests move the camera and the isolation set, so they cannot overlap.
Assert-Contains $probe 'chain = chain.then' 'Probe requests must run sequentially; parallel runs overwrite each other''s view state.'

# Delivery: the script has to reach the add-in payload, or the page loads a 404.
Assert-Contains $html 'buildai-anchor-probe.js' 'viewer-probe.html must load the anchor probe.'
Assert-Contains $p4 'buildai-anchor-probe.js' 'Plugin4 must copy the anchor probe into its payload.'
Assert-Contains $p5 'buildai-anchor-probe.js' 'Plugin5 must copy the anchor probe into its payload.'

Write-Host '[OK] Anchor probe: dual-frame output, redraw before hitTest, runtime calibration and payload delivery verified.' -ForegroundColor Green
exit 0
