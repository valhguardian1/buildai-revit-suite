$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

function Read-Utf8([string]$relative) {
  return [System.IO.File]::ReadAllText((Join-Path $root $relative), [System.Text.Encoding]::UTF8)
}
function Assert-Contains([string]$text, [string]$fragment, [string]$message) {
  if ($text.IndexOf($fragment, [StringComparison]::Ordinal) -lt 0) { throw $message }
}
function Assert-NotContains([string]$text, [string]$fragment, [string]$message) {
  if ($text.IndexOf($fragment, [StringComparison]::Ordinal) -ge 0) { throw $message }
}

$integration = Read-Utf8 'src\BuildAI.Core\Issues\IssueIntegrationClient.cs'
$publication = Read-Utf8 'src\BuildAI.Core\APS\ApsPublicationClient.cs'
$models = Read-Utf8 'src\BuildAI.Core\Issues\IssueModels.cs'
$fingerprints = Read-Utf8 'src\BuildAI.Core\Issues\IssueFingerprintStore.cs'
$logging = Read-Utf8 'src\BuildAI.Core\Logging\PluginLog.cs'
$clash = Read-Utf8 'src\Plugin5.ClashFormaIntegration\Issues\ClashIssueWorkflow.cs'
$arSt = Read-Utf8 'src\Plugin4.LinkComparatorAI\Issues\ComparatorIssueWorkflow.cs'
$clashUi = Read-Utf8 'src\Plugin5.ClashFormaIntegration\UI\ResultsWindow.cs'
$clashModel = Read-Utf8 'src\Plugin5.ClashFormaIntegration\Models\ClashItem.cs'
$clashRevit = Read-Utf8 'src\Plugin5.ClashFormaIntegration\Revit\ModelActionHandler.cs'
$arStRevit = Read-Utf8 'src\Plugin4.LinkComparatorAI\Revit\ComparatorActionHandler.cs'
$coordinationVisibility = Read-Utf8 'src\RevitCompat.Shared\CoordinationViewVisibility.cs'
$clashPreparation = Read-Utf8 'src\Plugin5.ClashFormaIntegration\Revit\PublishViewPreparationResult.cs'
$arStPreparation = Read-Utf8 'src\Plugin4.LinkComparatorAI\Revit\PublishViewPreparationResult.cs'
$props = Read-Utf8 'Directory.Build.props'
$wix = Read-Utf8 'installer\BuildAI.RevitSuite.wxs'
$buildScript = Read-Utf8 'build.ps1'
$protectionScript = Read-Utf8 'tools\Protect-BuildAI-Payload.ps1'

foreach ($region in @('US','EMEA','AUS','GBR','DEU','JPN','CAN','IND')) {
  Assert-Contains $integration ('"' + $region + '"') "Regional Issue validation is missing $region."
  Assert-Contains $publication ('"' + $region + '"') "Regional Model Derivative routing is missing $region."
}
foreach ($fragment in @(
  'ResolveRegionalIssueEnvironmentAsync',
  'APS OPERATION CONTEXT FROZEN',
  'APS MULTI-REGION SWEEP COMPLETE',
  'APS MULTI-REGION DEGRADED ACCEPTANCE',
  'catch (ApsHttpException ex)',
  'response.Headers.RetryAfter?.Delta',
  'NormalizeDerivativeUrn',
  # Renamed in 7.0.5. The old names encoded a wrong model: issues/v2/containers
  # is BIM 360 only and construction/issues/v1 is the CURRENT ACC API, not a
  # legacy fallback. Autodesk blocks ACC projects on the v2 route outright.
  'ResolveClashSubtypeBim360Async',
  'ResolveClashSubtypeAccAsync',
  'ApsIssueApiMode.AccIssuesV1',
  'ApsIssueApiMode.Bim360IssuesV2',
  'IsProbeRejection')) {
  Assert-Contains $integration $fragment "Multi-region/Issues invariant missing: $fragment"
}
foreach ($fragment in @(
  'relationships"]?["derivatives"]?["meta"]?["link"]?["href"]',
  'APS AUTHORITATIVE DERIVATIVE ROUTE',
  'BuildAuthoritativeDerivativeUrl',
  'ResolveCurrentVersionDerivativeUrn',
  'Scopes preserved:')) {
  Assert-Contains $publication $fragment "Authoritative multi-region derivative routing invariant missing: $fragment"
}
foreach ($fragment in @(
  'model.DerivativeManifestUrl',
  'authoritative current-version manifest probe',
  'BuildRegionalManifestUrl',
  '/modelderivative/v2/regions/')) {
  Assert-Contains $integration $fragment "Current-version regional manifest invariant missing: $fragment"
}
Assert-Contains $models 'APS TOKEN REFRESH DID NOT ROTATE TOKEN' 'Non-fatal rejected-token replay guard is missing.'
foreach ($fragment in @('IssueFingerprintStore','MarkCreated','SHA256.Create','created-fingerprints.json')) {
  Assert-Contains $fingerprints $fragment "Fingerprint journal invariant missing: $fragment"
}
foreach ($workflow in @($clash, $arSt)) {
  foreach ($fragment in @('ResolveRegionalIssueEnvironmentAsync','fingerprintStore.Contains','fingerprintStore.MarkCreated','PluginLog.BeginOperation')) {
    Assert-Contains $workflow $fragment "Mass-Issue safeguard missing: $fragment"
  }
# The pushpin position frame changed in 7.0.10. probe.Center is viewer-local
# and only meaningful inside the session that produced it, because the viewer
# picks globalOffset at load time from what it loaded. What is persisted must
# be offset-independent, so the model frame is written instead. The invariant
# still guarded: the position comes from the Viewer probe, not from Revit.
  foreach ($fragment in @('var pushpinSource = probe.Anchor','var pushpinX = pushpinSource.X','ViewerCoordinateProbe.ResolveBatchAsync')) {
    Assert-Contains $workflow $fragment "Frozen Viewer-first coordinate invariant missing: $fragment"
  }
}
foreach ($fragment in @('Check run ID:','Generated at UTC:')) {
  Assert-Contains $arSt $fragment "AR-ST Issue audit marker missing: $fragment"
}
Assert-Contains $clash 'AddDescriptionLine(lines, "Run", operationId)' 'Compact Clash Issue run marker is missing.'
Assert-Contains $clash 'probe.GlobalAnchor' 'Clash model-frame surface-anchor invariant is missing.'
Assert-Contains $arSt 'probe.GlobalAnchor' 'AR-ST surface-aware model-frame pushpin invariant is missing.'
foreach ($fragment in @('DeliveryAttempts <= 5','<local-path>','<email>','Pseudonymize("project"','IsPrivateIdentifierName')) {
  Assert-Contains $logging $fragment "Remote-log privacy/retry invariant missing: $fragment"
}
foreach ($fragment in @('CategoryGroupHeaderConverter','total: ','selected: ','Issues: ','Element A ID','Element B ID','Issue status','Source type A','Remove filter')) {
  Assert-Contains $clashUi $fragment "Clash grouping/filter invariant missing: $fragment"
}
Assert-Contains $clashModel 'WithLinkContext' 'Duplicate link names are not given display-only instance context.'
Assert-Contains $clashRevit 'MepComparisonViewComposer.Compose(doc, coordination, PluginContext.Report)' 'Clash coordination view is not scoped to the current MEP comparison.'
Assert-Contains $arStRevit 'CoordinationViewVisibility.EnsureFull(doc, coordination)' 'AR-ST coordination view is not protected by its full-visibility guard.'
foreach ($handler in @($clashRevit, $arStRevit)) {
  Assert-Contains $handler 'DocumentIsModifiedAfterPreparation = doc.IsModified' 'View-preparation changes are not propagated to synchronization decisions.'
}
Assert-Contains $arStRevit 'CoordinationViewVisibility.EnsureFull(doc, arSt)' 'BuildAI AR-ST is not restored by its owning AR-ST workflow before deliberate filtering.'
Assert-Contains $clashRevit 'AR-ST VIEW PRESERVED' 'Clash workflow does not enforce AR-ST view isolation.'
Assert-NotContains $clashRevit 'CoordinationViewVisibility.EnsureFull(doc, arSt)' 'Clash workflow must not restore or mutate BuildAI AR-ST.'
foreach ($preparation in @($clashPreparation, $arStPreparation)) {
  Assert-Contains $preparation 'DocumentIsModifiedAfterPreparation' 'Preparation result does not track changes made while repairing view visibility.'
  Assert-Contains $preparation 'DocumentWasModifiedBeforePreparation || DocumentIsModifiedAfterPreparation' 'View changes do not force safe synchronization.'
  Assert-Contains $preparation 'HostModelUid' 'The Revit model UID is not frozen inside the publication event.'
}
Assert-NotContains $clashRevit 'SetRelevantLinkVisibility(doc,coordination)' 'Clash publication still hides unrelated links in BuildAI Coordination.'
foreach ($fragment in @(
  'view.ViewTemplateId = ElementId.InvalidElementId',
  'category.CategoryType != CategoryType.Model',
  'view.SetCategoryHidden(category.Id, false)',
  'view.UnhideElements(hiddenLinks)',
  'view.SetFilterVisibility(filterId, true)',
  'view.SetWorksetVisibility(worksetId, WorksetVisibility.Visible)',
  'BuildAI Coordination visibility validation failed')) {
  Assert-Contains $coordinationVisibility $fragment "Coordination visibility invariant missing: $fragment"
}
foreach ($workflow in @($clash, $arSt)) {
  Assert-Contains $workflow 'PARTIAL:' 'BuildAI batch failure is still reported as complete success.'
  Assert-Contains $workflow 'DateTime.Now.ToString("yyyy-MM-dd")' 'Issue due date is not based on the local calendar date.'
}
foreach ($fragment in @('SaveIssueBatchResponse','result.Failed > 0','result.Saved + result.Updated < expected')) {
  Assert-Contains ($models + $integration) $fragment "BuildAI batch body validation is missing: $fragment"
}
Assert-Contains $integration 'The pushpin was preserved and the Issue was not created.' 'Pushpin rejection is not fatal for the individual Issue.'
Assert-NotContains $integration 'payload.LinkedDocuments = null' 'Issue client can still retry creation without a pushpin.'
Assert-NotContains $clash '[Viewer Coordinate Probe]' 'Diagnostic Viewer prefix remains in user-facing Clash titles.'
Assert-NotContains $clash 'Viewer Coordinate Probe 6.6.18' 'Obsolete diagnostic version remains in user-facing Clash fields.'
foreach ($ui in @($clashUi, (Read-Utf8 'src\Plugin4.LinkComparatorAI\UI\ResultsWindow.cs'))) {
  Assert-Contains $ui 'prep.HostModelUid' 'Issue workflow does not consume the model UID frozen by the Revit event.'
}
$currentVersionMatch = [regex]::Match($props, '<Version>(?<version>\d+\.\d+(?:\.\d+)?)</Version>')
if (-not $currentVersionMatch.Success) { throw 'Current assembly version was not found in Directory.Build.props.' }
$currentVersion = $currentVersionMatch.Groups['version'].Value
Assert-Contains $wix ('Version="' + $currentVersion + '"') 'MSI version does not match Directory.Build.props.'
foreach ($version in @('2023','2024','2025','2026')) {
  Assert-Contains $wix ('payload\' + $version + '\**') "MSI payload missing for Revit $version."
  Assert-Contains $wix ('REVIT' + $version + 'INSTALLED') "MSI Revit $version detection property is missing."
  Assert-Contains $wix ('Key="SOFTWARE\Autodesk\Revit\' + $version + '"') "MSI Revit $version diagnostic registry search is missing."
}
Assert-Contains $buildScript '& wix msi decompile $msi -o $decompiledWxs' 'MSI verification must inspect the built MSI through WiX decompilation.'
Assert-Contains $buildScript '$fileNodes.Count -ne $expectedFiles.Count' 'MSI verification must compare the full staged and MSI file counts.'
Assert-Contains $buildScript '$actualCount -ne [int]$entry.Value' 'MSI verification must compare staged and MSI filename multiplicities.'
Assert-NotContains $buildScript "Start-Process -FilePath 'msiexec.exe'" 'MSI verification must not depend on conditional administrative installation.'
Assert-NotContains $buildScript '$database = $installer.GetType().InvokeMember(''OpenDatabase''' 'PowerShell 7-incompatible Windows Installer COM verification was reintroduced.'
Assert-Contains $buildScript '& $protectionScript -PayloadRoot $payloadRoot -Versions $Versions' 'Protected build must preserve Versions as a string[] parameter.'
Assert-NotContains $buildScript "powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path `$root 'tools\Protect-BuildAI-Payload.ps1')" 'Protected build must not flatten Versions through a child PowerShell -File process.'
Assert-Contains $protectionScript "SetEnvironmentVariable('DOTNET_ROLL_FORWARD', 'Major', 'Process')" 'Pinned net9.0 Obfuscar must be allowed to run on the installed .NET 10 runtime.'
Assert-Contains $protectionScript "SetEnvironmentVariable('DOTNET_ROLL_FORWARD', `$previousDotnetRollForward, 'Process')" 'Protection must restore the caller DOTNET_ROLL_FORWARD setting.'

Write-Host '[OK] BuildAI 7.0.4 coordination visibility, multi-region, privacy, duplicate and frozen-coordinate invariants verified.' -ForegroundColor Green
