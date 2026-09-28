$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

# ViewerCoordinateProbe.cs now carries the reviewed pair-anchor DTO. A frozen
# file hash would reject every intentional coordinate-field addition before the
# compiler runs, so protect the host and batch-index invariants semantically.
$probeHost = Get-Content -LiteralPath (Join-Path $root 'src\ViewerProbe.Shared\ViewerCoordinateProbe.cs') -Raw
foreach ($fragment in @(
  'CoreWebView2Environment.GetAvailableBrowserVersionString()',
  'thread.SetApartmentState(ApartmentState.STA)',
  'outcomes.GroupBy(x => x.Index).Where(x => x.Count() != 1)',
  'Enumerable.Range(0, _requests.Count)',
  'public ViewerProbePoint GlobalAnchor')) {
  if ($probeHost.IndexOf($fragment, [StringComparison]::Ordinal) -lt 0) {
    throw "Viewer probe host/batch invariant missing: $fragment"
  }
}

$ui = Get-Content -LiteralPath (Join-Path $root 'src\Plugin5.ClashFormaIntegration\UI\ResultsWindow.cs') -Raw
$model = Get-Content -LiteralPath (Join-Path $root 'src\Plugin5.ClashFormaIntegration\Models\ClashItem.cs') -Raw
$workflow = Get-Content -LiteralPath (Join-Path $root 'src\Plugin5.ClashFormaIntegration\Issues\ClashIssueWorkflow.cs') -Raw
$logging = Get-Content -LiteralPath (Join-Path $root 'src\BuildAI.Core\Logging\PluginLog.cs') -Raw
$options = Get-Content -LiteralPath (Join-Path $root 'src\BuildAI.Core\Configuration\BuildAiOptions.cs') -Raw
$installer = Get-Content -LiteralPath (Join-Path $root 'installer\BuildAI.RevitSuite.wxs') -Raw
$clashHandler = Get-Content -LiteralPath (Join-Path $root 'src\Plugin5.ClashFormaIntegration\Revit\ModelActionHandler.cs') -Raw
$arStHandler = Get-Content -LiteralPath (Join-Path $root 'src\Plugin4.LinkComparatorAI\Revit\ComparatorActionHandler.cs') -Raw
$visibility = Get-Content -LiteralPath (Join-Path $root 'src\RevitCompat.Shared\CoordinationViewVisibility.cs') -Raw
$clashPreparation = Get-Content -LiteralPath (Join-Path $root 'src\Plugin5.ClashFormaIntegration\Revit\PublishViewPreparationResult.cs') -Raw
$arStPreparation = Get-Content -LiteralPath (Join-Path $root 'src\Plugin4.LinkComparatorAI\Revit\PublishViewPreparationResult.cs') -Raw

foreach ($fragment in @('Group by categories','Select visible group','Add filter','VisibleIssueRows()','Confirm Issue creation')) {
  if ($ui.IndexOf($fragment, [StringComparison]::Ordinal) -lt 0) { throw "Clash UI safe enhancement missing: $fragment" }
}
foreach ($fragment in @('DisplaySourceA','DisplaySourceB','CategoryPair','NormalizeSourceForDisplay')) {
  if ($model.IndexOf($fragment, [StringComparison]::Ordinal) -lt 0) { throw "Display-only source normalization missing: $fragment" }
}
foreach ($fragment in @('AddDescriptionLine(lines, "Primary"','AddDescriptionLine(lines, "Secondary"','ElementId ','return LimitDescription(string.Join("\n", lines), 1000)')) {
  if ($workflow.IndexOf($fragment, [StringComparison]::Ordinal) -lt 0) { throw "Detailed Clash Issue description missing: $fragment" }
}
# The pushpin position frame changed in 7.0.10. probe.Center is viewer-local
# and only meaningful inside the session that produced it, because the viewer
# picks globalOffset at load time from what it loaded. What is persisted must
# be offset-independent, so the model frame is written instead. The invariant
# still guarded: the position comes from the Viewer probe, not from Revit.
foreach ($fragment in @('BuildAiViewNames.Coordination','ViewerCoordinateProbe.ResolveBatchAsync','var pushpinSource = probe.Anchor','var pushpinX = pushpinSource.X','PUSHPIN COORDINATE FRAME')) {
  if ($workflow.IndexOf($fragment, [StringComparison]::Ordinal) -lt 0) { throw "Working 6.6.24 Viewer-first pushpin invariant missing: $fragment" }
}
foreach ($fragment in @('Pseudonymize','SanitizeData','IsSecretName')) {
  if ($logging.IndexOf($fragment, [StringComparison]::Ordinal) -lt 0) { throw "Remote log privacy guard missing: $fragment" }
}
if ($options.IndexOf('RemoteLoggingEnabled { get; set; } = true', [StringComparison]::Ordinal) -lt 0) {
  throw 'Automatic BuildAI log delivery must be enabled by default.'
}
foreach ($version in @('2023','2024','2025','2026')) {
  if ($installer.IndexOf(('payload\' + $version + '\**'), [StringComparison]::Ordinal) -lt 0) { throw "Installer payload missing for Revit $version" }
}

if ($clashHandler.IndexOf('MepComparisonViewComposer.Compose(doc, coordination, PluginContext.Report)', [StringComparison]::Ordinal) -lt 0) {
  throw 'Scoped MEP coordination publication safeguard is missing.'
}
if ($arStHandler.IndexOf('CoordinationViewVisibility.EnsureFull(doc, coordination)', [StringComparison]::Ordinal) -lt 0) {
  throw 'AR-ST coordination publication safeguard is missing.'
}
foreach ($handler in @($clashHandler, $arStHandler)) {
  if ($handler.IndexOf('DocumentIsModifiedAfterPreparation = doc.IsModified', [StringComparison]::Ordinal) -lt 0) {
    throw 'View-preparation synchronization safeguard is missing.'
  }
}
if ($arStHandler.IndexOf('CoordinationViewVisibility.EnsureFull(doc, arSt)', [StringComparison]::Ordinal) -lt 0) {
  throw 'AR-ST publication safeguard missing.'
}
foreach ($fragment in @('viewerState.globalOffset = {', 'if (typeof viewer.showAll ===', 'viewer.setAggregateSelection(aggregateSelection)', 'isolated: entry.id.slice()', 'ghostHidden: true', 'MAX_SURFACE_TRIANGLES = 20000', 'surface-pair-midpoint/', 'fragments.getWorldBounds(fragmentId, fragmentBounds)')) {
  if ((Get-Content -LiteralPath (Join-Path $root 'src\ViewerProbe.Shared\viewer-probe.html') -Raw).IndexOf($fragment, [StringComparison]::Ordinal) -lt 0) {
    throw "Pushpin state/camera safeguard missing: $fragment"
  }
}
if ($clashHandler.IndexOf('SetRelevantLinkVisibility(doc,coordination)', [StringComparison]::Ordinal) -ge 0) {
  throw 'Clash link isolation must not hide links in BuildAI Coordination.'
}
foreach ($fragment in @('view.ViewTemplateId = ElementId.InvalidElementId', 'view.SetCategoryHidden(category.Id, false)', 'view.UnhideElements(hiddenLinks)', 'BuildAI Coordination visibility validation failed')) {
  if ($visibility.IndexOf($fragment, [StringComparison]::Ordinal) -lt 0) { throw "Coordination visibility safeguard missing: $fragment" }
}
foreach ($preparation in @($clashPreparation, $arStPreparation)) {
  foreach ($fragment in @('DocumentIsModifiedAfterPreparation', 'DocumentWasModifiedBeforePreparation || DocumentIsModifiedAfterPreparation')) {
    if ($preparation.IndexOf($fragment, [StringComparison]::Ordinal) -lt 0) { throw "Safe synchronization safeguard missing: $fragment" }
  }
}

Write-Host '[OK] Frozen pushpin behavior and 7.0.4 coordination/publication safeguards verified.' -ForegroundColor Green
