$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$clientPath = Join-Path $root 'src\BuildAI.Core\APS\ApsPublicationClient.cs'
$text = Get-Content -LiteralPath $clientPath -Raw

$requiredMarkers = @(
    'AEC MODEL DATA WAIT START',
    'AEC MODEL DATA WAIT ATTEMPT',
    'AEC MODEL DATA READY',
    # 7.0.7 replaced the fixed retry ladder with a wall-clock budget plus a
    # stall detector. The ladder could never span the 12-20 minutes a large
    # federated model needs, so asserting its presence would now pin a defect
    # in place. The guarantee being checked is unchanged: the wait must be
    # bounded and must track translation progress.
    'AecModelDataTotalBudget',
    'AecModelDataStallBudget',
    'Refresh Model Derivative manifest for Autodesk.AEC.ModelData',
    'IsTemporaryApsStatus',
    'statusCode == 401 || statusCode == 403',
    'refPointTransformation[12]'
)

foreach ($marker in $requiredMarkers) {
    if (-not $text.Contains($marker)) {
        throw "AEC Model Data readiness guard is missing required marker: $marker"
    }
}

if ($text -notmatch '202\s*\|\|\s*statusCode\s*==\s*204\s*\|\|\s*statusCode\s*==\s*404') {
    throw 'AEC Model Data retry guard does not classify HTTP 202, 204 and 404 as temporary.'
}

if ($text -notmatch 'statusCode\s*==\s*408\s*\|\|\s*statusCode\s*==\s*429\s*\|\|\s*statusCode\s*>=\s*500') {
    throw 'AEC Model Data retry guard does not classify HTTP 408, 429 and 5xx as temporary.'
}

if ($text -match 'Revit-coordinate fallback:\s*used') {
    throw 'Unsafe Revit-coordinate fallback was introduced.'
}

Write-Host '[OK] AEC Model Data download refreshes the manifest and retries only transient APS readiness failures.'
