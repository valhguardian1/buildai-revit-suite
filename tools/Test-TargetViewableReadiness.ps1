$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$publication = Join-Path $root "src\BuildAI.Core\APS\ApsPublicationClient.cs"

Write-Host "==> Checking target-viewable Model Derivative readiness"

if (-not (Test-Path -LiteralPath $publication -PathType Leaf)) {
    throw "APS publication client is missing: $publication"
}

$text = Get-Content -LiteralPath $publication -Raw
foreach ($required in @(
    'WaitForDerivativeReadyAsync(derivativeUrn, preferredView',
    'EvaluateRequiredViewableReadiness(root, preferredView)',
    'readiness.HasTerminalFailure',
    'readiness.IsReady && rootReady',
    'IsCompleteProgress(progress)',
    'MODEL DERIVATIVE TARGET VIEWABLE READY',
    'PropertyDatabase',
    'Autodesk.AEC.ModelData',
    'graphicsIdentity=',
    'viewableIdentity=',
    'Root status: '
    'ROOT LAG TOLERATED'
    'TimeSpan.FromSeconds(45)'
)) {
    if ($text -notmatch [regex]::Escape($required)) {
        throw "Target-viewable readiness marker is missing: $required"
    }
}

foreach ($forbidden in @(
    'WaitForDerivativeReadyAsync(derivativeUrn, token'
)) {
    if ($text -match [regex]::Escape($forbidden)) {
        throw "Obsolete root-only Model Derivative readiness remains: $forbidden"
    }
}

if ([regex]::IsMatch($text, 'if\s*\(status\s*==\s*"success"\)\s*\{[\s\S]{0,400}?return\s+root;')) {
    throw "Obsolete root-status-only Model Derivative success path remains."
}

$rootFailure = $text.IndexOf('if (status == "failed" || status == "timeout")', [System.StringComparison]::Ordinal)
$targetReady = $text.IndexOf('if (readiness.IsReady && rootReady)', [System.StringComparison]::Ordinal)
if ($rootFailure -lt 0 -or $targetReady -lt 0 -or $rootFailure -gt $targetReady) {
    throw "Root failed/timeout protection must run before the target-viewable success path."
}

Write-Host "[OK] The exact viewable requires mandatory resources and tolerates bounded root-manifest lag."
