$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

function Read-Utf8([string]$path) {
    return [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8)
}

$resolver = Read-Utf8 (Join-Path $root 'src\BuildAI.Core\APS\ApsObjectResolver.cs')
$viewer = Read-Utf8 (Join-Path $root 'src\ViewerProbe.Shared\viewer-probe.html')
$probe = Read-Utf8 (Join-Path $root 'src\ViewerProbe.Shared\ViewerCoordinateProbe.cs')
$arSt = Read-Utf8 (Join-Path $root 'src\Plugin4.LinkComparatorAI\Issues\ComparatorIssueWorkflow.cs')
$arStView = Read-Utf8 (Join-Path $root 'src\Plugin4.LinkComparatorAI\Revit\ComparatorActionHandler.cs')
$clash = Read-Utf8 (Join-Path $root 'src\Plugin5.ClashFormaIntegration\Issues\ClashIssueWorkflow.cs')

$failures = New-Object System.Collections.Generic.List[string]
if ($resolver.Contains('APS FULL PROPERTIES REQUEST')) { $failures.Add('Full /properties fallback marker still exists.') }
if ($clash.Contains('ResolveObjectAsync(')) { $failures.Add('Clash/MEP workflow still depends on Model Derivative object lookup.') }
if (-not $arSt.Contains('ArStIssuePayloadAdapter.Build') -or -not $arSt.Contains('ResolveExternalIdByObjectIdAsync')) { $failures.Add('AR-ST authoritative externalId/dbId adapter is missing.') }
if (-not $viewer.Contains('findDbIdCandidates')) { $failures.Add('Viewer candidate enumeration is missing.') }
if (-not $viewer.Contains('getCachedExternalIdMapping')) { $failures.Add('externalId mapping cache is missing.') }
if (-not $viewer.Contains('fragmentCount')) { $failures.Add('Geometry candidate diagnostics are missing.') }
if (-not $viewer.Contains('__startProbeBatch')) { $failures.Add('Viewer batch entry point is missing.') }
if (-not $probe.Contains('ResolveBatchAsync')) { $failures.Add('C# Viewer batch API is missing.') }
if (-not $arSt.Contains('ResolveBatchAsync') -or -not $clash.Contains('ResolveBatchAsync')) { $failures.Add('Issue workflows do not use the Viewer batch API.') }
if (-not $arStView.Contains('SetSelectedLinkVisibility')) { $failures.Add('AR-ST selected-link view filtering is missing.') }

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Host ('[FAIL] ' + $_) -ForegroundColor Red }
    throw 'Large-model Issue resolution validation failed.'
}

Write-Host '[OK] Selected-link publication, Viewer-first geometry resolution and batch reuse are present.' -ForegroundColor Green
