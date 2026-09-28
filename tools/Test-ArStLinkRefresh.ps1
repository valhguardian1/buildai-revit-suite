param(
    [string]$Root = (Join-Path $PSScriptRoot '..')
)

$ErrorActionPreference = 'Stop'
$service = Join-Path $Root 'src\Plugin4.LinkComparatorAI\Comparison\LinkRefreshService.cs'
$commands = Join-Path $Root 'src\Plugin4.LinkComparatorAI\Revit\Commands.cs'
$handler = Join-Path $Root 'src\Plugin4.LinkComparatorAI\Revit\ComparatorActionHandler.cs'
$window = Join-Path $Root 'src\Plugin4.LinkComparatorAI\UI\ResultsWindow.cs'
$resolver = Join-Path $Root 'src\Plugin4.LinkComparatorAI\Comparison\LinkResolver.cs'

foreach ($path in @($service, $commands, $handler, $window, $resolver)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Required AR-ST refresh source is missing: $path" }
}

$serviceText = Get-Content -LiteralPath $service -Raw
$commandsText = Get-Content -LiteralPath $commands -Raw
$handlerText = Get-Content -LiteralPath $handler -Raw
$windowText = Get-Content -LiteralPath $window -Raw
$resolverText = Get-Content -LiteralPath $resolver -Raw

foreach ($marker in @('AR-ST DATA REFRESH START', 'AR-ST LINK RELOAD START', 'AR-ST LINK AFTER RELOAD', 'AR-ST DATA REFRESH COMPLETE')) {
    if ($serviceText -notmatch [regex]::Escape($marker)) { throw "Missing AR-ST refresh diagnostic marker: $marker" }
}
if ($serviceText -notmatch 'linkType\.Load\s*\(') { throw 'Selected Revit link types are not reloaded before comparison.' }
if ($serviceText -notmatch 'GetLinkDocument\s*\(' -or $serviceText -notmatch 'GetTotalTransform\s*\(') { throw 'Linked documents and transforms are not reacquired after reload.' }
if ($serviceText -notmatch 'reloadedTypeIds') { throw 'Link types are not deduplicated before reload.' }
if ($commandsText -notmatch 'LinkRefreshService\.RefreshSelectedLinks') { throw 'Compare AR-ST command does not refresh selected links.' }
if ($handlerText -notmatch 'LinkRefreshService\.RefreshSelectedLinks') { throw 'Issue-time AR-ST recalculation does not refresh selected links.' }
if ($windowText -notmatch 'BeginComparisonRefresh' -or $windowText -notmatch '_grid\.ItemsSource=null') { throw 'Old AR-ST rows are not cleared when refresh starts.' }
if ($windowText -notmatch '_populateVersion') { throw 'Overlapping asynchronous result loads are not guarded.' }
if ($resolverText -notmatch 'found == null && string\.IsNullOrWhiteSpace\(sourceId\)') { throw 'A missing saved link selection can still fall back silently to another link.' }

Write-Host '[OK] AR-ST comparisons reload selected links and clear stale UI results.' -ForegroundColor Green
exit 0
