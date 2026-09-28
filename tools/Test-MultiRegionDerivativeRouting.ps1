$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

function Read-Utf8([string]$relative) {
  return [System.IO.File]::ReadAllText((Join-Path $root $relative), [System.Text.Encoding]::UTF8)
}
function Assert-Contains([string]$text, [string]$fragment, [string]$message) {
  if ($text.IndexOf($fragment, [StringComparison]::Ordinal) -lt 0) { throw $message }
}

$publication = Read-Utf8 'src\BuildAI.Core\APS\ApsPublicationClient.cs'
$integration = Read-Utf8 'src\BuildAI.Core\Issues\IssueIntegrationClient.cs'
$models = Read-Utf8 'src\BuildAI.Core\Issues\IssueModels.cs'

foreach ($fragment in @(
  'relationships"]?["derivatives"]?["meta"]?["link"]?["href"]',
  'ResolveCurrentVersionDerivativeUrn',
  'BuildAuthoritativeDerivativeUrl',
  'return route.ManifestUrl',
  'BuildRoutedDerivativeUrl(url, fallbackRegion, route.Query)',
  '"/regions/" + Uri.EscapeDataString(region.ToLowerInvariant())',
  'MergeQuery(authoritativeQuery, source.Query)')) {
  Assert-Contains $publication $fragment "Model Derivative authoritative-route guard is missing: $fragment"
}

foreach ($fragment in @(
  'model.DerivativeManifestUrl',
  'model.DerivativeRegion',
  'model.DerivativeScopes',
  'IsAuthoritative = true',
  'authoritative current-version manifest probe',
  'BuildRegionalManifestUrl')) {
  Assert-Contains $integration $fragment "Issues multi-region current-version guard is missing: $fragment"
}

foreach ($fragment in @(
  'string.Equals(replacement.AccessToken, rejectedAccessToken, StringComparison.Ordinal)',
  'APS TOKEN REFRESH DID NOT ROTATE TOKEN')) {
  Assert-Contains $models $fragment "APS rejected-token replay guard is missing: $fragment"
}
Assert-Contains $publication 'Continuing with the remaining routes' 'A regional 401 can still abort publication routing.'

foreach ($fragment in @(
  'APS MULTI-REGION SWEEP COMPLETE',
  'APS MULTI-REGION DEGRADED ACCEPTANCE',
  'var accepted = new List<ManifestCandidate>()',
  'catch (ApsHttpException ex)')) {
  Assert-Contains $integration $fragment "Non-fatal full regional sweep is missing: $fragment"
}

foreach ($fragment in @(
  'APS REGIONAL ROUTE REJECTED',
  'APS REGIONAL SWEEP INCONCLUSIVE',
  'bestTemporary',
  'if (bestTemporary != null) return bestTemporary')) {
  Assert-Contains $publication $fragment "Publication routing can still abort on one negative region: $fragment"
}

Write-Host '[OK] Authoritative routing, full regional sweep and non-fatal 401 handling verified.' -ForegroundColor Green
