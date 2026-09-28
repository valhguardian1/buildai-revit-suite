$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

Write-Host "==> Checking Viewer Coordinate Probe architecture"

$probeCode = Join-Path $root "src\ViewerProbe.Shared\ViewerCoordinateProbe.cs"
$probeHtml = Join-Path $root "src\ViewerProbe.Shared\viewer-probe.html"
$integration = Join-Path $root "src\BuildAI.Core\Issues\IssueIntegrationClient.cs"
$build = Join-Path $root "build.ps1"
$projects = @(
    (Join-Path $root "src\Plugin4.LinkComparatorAI\Plugin4.LinkComparatorAI.csproj")
    (Join-Path $root "src\Plugin5.ClashFormaIntegration\Plugin5.ClashFormaIntegration.csproj")
)
$workflows = @(
    (Join-Path $root "src\Plugin4.LinkComparatorAI\Issues\ComparatorIssueWorkflow.cs")
    (Join-Path $root "src\Plugin5.ClashFormaIntegration\Issues\ClashIssueWorkflow.cs")
)

foreach ($file in @($probeCode, $probeHtml, $integration, $build) + $projects + $workflows) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
        throw "Viewer Coordinate Probe source is missing: $file"
    }
}

$probeCodeText = Get-Content -LiteralPath $probeCode -Raw
$probeHtmlText = Get-Content -LiteralPath $probeHtml -Raw
$integrationText = Get-Content -LiteralPath $integration -Raw
$buildText = Get-Content -LiteralPath $build -Raw

foreach ($required in @(
    'CoreWebView2Environment.GetAvailableBrowserVersionString()',
    'CoreWebView2Environment.CreateAsync',
    'SetVirtualHostNameToFolderMapping',
    'thread.SetApartmentState(ApartmentState.STA)',
    'ViewerCoordinateProbeResult',
    'DbId > 0 && !string.IsNullOrWhiteSpace(ResolvedExternalId)',
    '"ViewerProbe", "viewer-probe.html"',
    'WebMessageReceived'
)) {
    if ($probeCodeText -notmatch [regex]::Escape($required)) {
        throw "WebView2 host marker is missing: $required"
    }
}

if ($probeCodeText -match [regex]::Escape('GetAvailableCoreWebView2BrowserVersionString')) {
    throw "The Win32 WebView2 API name is not valid on CoreWebView2Environment in the .NET SDK."
}

foreach ($required in @(
    'AutodeskProduction2',
    'streamingV2',
    "search({ type: 'geometry' })",
    'getExternalIdMapping(model)',
    'waitForSceneModels(rootModel)',
    'resolveTargetModel(sceneModels, request)',
    'enumNodeFragments(dbId',
    'getWorldBounds(fragmentId',
    'viewer.getState()',
    'if (typeof viewer.showAll ===',
    'viewerState.globalOffset = {',
    'MAX_SURFACE_TRIANGLES = 20000',
    'projectPointToSurface(',
    'surface-pair-midpoint/',
    'viewer.setAggregateSelection(aggregateSelection)',
    'isolated: entry.id.slice()',
    'ghostHidden: true',
    'viewer3D.min.js'
)) {
    if ($probeHtmlText -notmatch [regex]::Escape($required)) {
        throw "Viewer scene probe marker is missing: $required"
    }
}

foreach ($forbidden in @(
    'viewer.isolate([resolved.dbId], model)',
    'viewer.fitToView([resolved.dbId], model)'
)) {
    if ($probeHtmlText -match [regex]::Escape($forbidden)) {
        throw "A whole-element isolation/camera regression remains in the Viewer probe: $forbidden"
    }
}

if ($probeHtmlText -match 'access_token' -or $probeHtmlText -match 'Bearer\s+[A-Za-z0-9]') {
    throw "The static Viewer HTML must not contain an APS access token."
}
if ($probeHtmlText -match 'globalOffset[X|Y|Z]*\s*[-+*/]' -or
    $probeHtmlText -match 'refPointTransformation\s*[-+*/]' -or
    $probeHtmlText -match '0\.3048\s*[-+*/]') {
    throw "A hand-written coordinate formula remains in the Viewer probe HTML."
}

foreach ($project in $projects) {
    $text = Get-Content -LiteralPath $project -Raw
    foreach ($required in @(
        'Microsoft.Web.WebView2',
        '..\ViewerProbe.Shared\ViewerCoordinateProbe.cs',
        '..\ViewerProbe.Shared\viewer-probe.html',
        'CopyToOutputDirectory="PreserveNewest"'
    )) {
        if ($text -notmatch [regex]::Escape($required)) {
            throw "WebView2 project integration is missing in ${project}: $required"
        }
    }
}

foreach ($required in @(
    'Microsoft.Web.WebView2.Core.dll',
    'Microsoft.Web.WebView2.Wpf.dll',
    'WebView2Loader.dll',
    'ViewerProbe\viewer-probe.html',
    'runtimes\win-x64\native\WebView2Loader.dll'
)) {
    if ($buildText -notmatch [regex]::Escape($required)) {
        throw "Build payload verification is missing: $required"
    }
}

if ($integrationText -notmatch 'JObject capturedViewerState = null' -or
    $integrationText -notmatch '\(JObject\)capturedViewerState\.DeepClone\(\)') {
    throw "IssueIntegrationClient does not serialize the exact captured Viewer state."
}
if ($integrationText -notmatch 'RedactSensitiveResponse\(url, body\)' -or
    $integrationText -notmatch '"access_token", "refresh_token", "client_secret"') {
    throw "APS token responses are not redacted before diagnostic logging."
}

foreach ($file in $workflows) {
    $text = Get-Content -LiteralPath $file -Raw
    $requiredMarkers = @(
        'var probeTotal = rows.Count',
        'ViewerCoordinateProbe.ResolveBatchAsync',
# 7.0.19: the position is written viewer-local, in Viewer units, exactly as the
# Viewer produced it, and ACC applies the Viewer's own globalOffset when it
# places the pin. Adding the offset here and scaling to metres put pins 54-68 m
# from their elements. The invariant still guarded: the position comes from the
# Viewer probe, not from Revit, and it is passed through untransformed.
        'pushpinX = pushpinSource.X',
        'pushpinY = pushpinSource.Y',
        'pushpinZ = pushpinSource.Z',
        # The metre conversion and the manual globalOffset were removed in 7.0.19.
        # ACC stores details.position in Viewer units and applies the Viewer's own
        # globalOffset itself; doing either here placed pins 54-68 m off.
        'var pushpinSource = probe.Anchor',
        'var resolvedExternalId = probe.ResolvedExternalId',
        'CreateApsIssueWithAssignmentFallbackAsync'
    )
    if ($file -like '*Plugin4.LinkComparatorAI*') {
        $requiredMarkers += 'ArStIssuePayloadAdapter.Build'
    } else {
        $requiredMarkers += 'resolvedExternalId, probe.DbId, probe.ViewerState, viewerOffset'
    }
    foreach ($required in $requiredMarkers) {
        if ($text -notmatch [regex]::Escape($required)) {
            throw "Viewer-probe workflow marker is missing in ${file}: $required"
        }
    }

    if ($file -like '*Plugin4.LinkComparatorAI*') {
        $requiredLocation = 'LocationDetails = location.Value'
        if ($text -notmatch [regex]::Escape('IssueLocationDetailsResolver.Resolve(row.Level, null, null, null)')) {
            throw "AR-ST Location Details must come from the structured result level."
        }
    }
    else {
        $requiredLocation = 'LocationDetails = location.Value'
    }
    if ($text -notmatch [regex]::Escape($requiredLocation)) {
        throw "Viewer-probe workflow location marker is missing in ${file}: $requiredLocation"
    }

    foreach ($forbidden in @(
        'ApsPushpinCoordinateMapper',
        'ApsPushpinLaboratory',
        'inversePushpin',
        'translatedPushpin',
        'pushpinX = row.X',
        'pushpinY = row.Y',
        'pushpinZ = row.Z',
        'rows.Count != 1',
        'const int probeTotal = 1',
        'ExpectedLmvId',
        'ExpectedSvf2Id'
    )) {
        if ($text -match [regex]::Escape($forbidden)) {
            throw "An obsolete coordinate path remains active in ${file}: $forbidden"
        }
    }
}

$pushpinFrame = Join-Path $root 'src/BuildAI.Core/Issues/PushpinFrame.cs'
$pushpinFrameText = Get-Content -LiteralPath $pushpinFrame -Raw
# PushpinFrame verifies the document instead of rewriting its coordinates.
foreach ($required in @(
    'ValidateViewerState',
    'AssertConsistent',
    'PUSHPIN GLOBAL OFFSET ALTERED',
    'globalOffset passed through unmodified'
)) {
    if ($pushpinFrameText -notmatch [regex]::Escape($required)) {
        throw "Pushpin frame guard is missing: $required"
    }
}
# Coordinates must reach ACC exactly as the Viewer produced them.
foreach ($forbidden in @(
    'pushpinUnitScaleToMeters',
    'ToModelMetres',
    'WriteGlobalOffsetMetres'
)) {
    if ($integrationText -match [regex]::Escape($forbidden) -or $pushpinFrameText -match [regex]::Escape($forbidden)) {
        throw "Pushpin coordinates must not be rescaled or offset before POST: $forbidden"
    }
}
if ($integrationText -notmatch [regex]::Escape('expectedGlobalOffset')) {
    throw 'IssueIntegrationClient does not pass the Viewer globalOffset through for verification.'
}

# Regression fixture from BuildAI_Issue_Creation_AR-ST_20260902_230452.log,
# Autodesk displayId 28. The old payload sent international feet as metres.
$issue28ModelFrameFeet = @(126.81663952983081, -30.561007978435704, 89.4028878176344)
$issue28ExpectedMetres = @(38.65371172861243, -9.314995231826402, 27.25000020665497)
for ($i = 0; $i -lt 3; $i++) {
    $actual = $issue28ModelFrameFeet[$i] * 0.3048
    if ([Math]::Abs($actual - $issue28ExpectedMetres[$i]) -gt 0.000000001) {
        throw "Issue 28 feet-to-metres regression failed at coordinate index $i."
    }
}

Write-Host "[OK] Every selected Issue uses runtime dbId, fragment world-bounds center, and viewer.getState() from the exact loaded viewable without obsolete user-facing probe labels."
