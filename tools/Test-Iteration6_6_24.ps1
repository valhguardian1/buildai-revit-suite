$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

$models = Get-Content -LiteralPath (Join-Path $root 'src\BuildAI.Core\Issues\IssueModels.cs') -Raw
$issues = Get-Content -LiteralPath (Join-Path $root 'src\BuildAI.Core\Issues\IssueIntegrationClient.cs') -Raw
$publication = Get-Content -LiteralPath (Join-Path $root 'src\BuildAI.Core\APS\ApsPublicationClient.cs') -Raw
$viewer = Get-Content -LiteralPath (Join-Path $root 'src\ViewerProbe.Shared\ViewerCoordinateProbe.cs') -Raw

foreach ($fragment in @(
  'TimeSpan.FromSeconds(120)',
  'APS TOKEN REFRESH START',
  'APS TOKEN REFRESH SUCCESS',
  'SemaphoreSlim',
  'rejectedAccessToken'
)) {
  if ($models.IndexOf($fragment, [System.StringComparison]::Ordinal) -lt 0) {
    throw "6.6.24 APS token lifecycle guard is incomplete: $fragment"
  }
}

foreach ($source in @($issues, $publication)) {
  foreach ($fragment in @('attempt == 0', 'IsExpiredApsTokenResponse', 'EnsureFreshAsync', 'usedAccessToken')) {
    if ($source.IndexOf($fragment, [System.StringComparison]::Ordinal) -lt 0) {
      throw "6.6.24 APS 401 retry guard is incomplete: $fragment"
    }
  }
}

# Publication has a dedicated two-attempt authentication loop. The Issues
# client has a wider bounded loop for 429/5xx responses, but its 401 branch is
# still restricted to attempt zero above, so it also performs one auth retry.
if ($publication.IndexOf('attempt < 2', [System.StringComparison]::Ordinal) -lt 0) {
  throw '6.6.24 APS publication retry loop must stop after two authentication attempts.'
}
if ($issues.IndexOf('attempt + 1 < maxAttempts', [System.StringComparison]::Ordinal) -lt 0) {
  throw '7.0.3 Issues retry loop must keep bounded 429/5xx retries separate from the single 401 retry.'
}

if ($viewer.IndexOf('await token.EnsureFreshAsync(cancellationToken, diagnostics)', [System.StringComparison]::Ordinal) -lt 0) {
  throw 'Viewer Coordinate Probe must refresh a nearly expired APS token before WebView2 starts.'
}

if (($issues.Split([string[]]@('FetchApsTokenAsync'), [System.StringSplitOptions]::None).Length - 1) -lt 3) {
  throw 'BuildAI APS token retrieval is not reusable by the automatic refresh callback.'
}

Write-Host '[OK] 6.6.24 APS proactive refresh and single AUTH-006 retry verified.' -ForegroundColor Green
