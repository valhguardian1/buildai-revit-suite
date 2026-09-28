$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

function Read-Utf8([string]$relative) {
  $path = Join-Path $root $relative
  if (-not (Test-Path -LiteralPath $path)) { throw "Chunked clash source is missing: $relative" }
  return [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8)
}
function Assert-Contains([string]$text, [string]$fragment, [string]$message) {
  if ($text.IndexOf($fragment, [StringComparison]::Ordinal) -lt 0) { throw $message }
}
function Assert-NotContains([string]$text, [string]$fragment, [string]$message) {
  if ($text.IndexOf($fragment, [StringComparison]::Ordinal) -ge 0) { throw $message }
}

$session = Read-Utf8 'src\Plugin5.ClashFormaIntegration\Clash\ClashSession.cs'
$engine  = Read-Utf8 'src\Plugin5.ClashFormaIntegration\Clash\ClashEngine.cs'
$proxy   = Read-Utf8 'src\Plugin5.ClashFormaIntegration\Clash\ElementProxy.cs'
$cursor  = Read-Utf8 'src\Plugin5.ClashFormaIntegration\Models\ClashCursor.cs'
$ui      = Read-Utf8 'src\Plugin5.ClashFormaIntegration\UI\ResultsWindow.cs'
$report  = Read-Utf8 'src\Plugin5.ClashFormaIntegration\Models\ClashItem.cs'

# The two phases must stay separate: collection and pairing are cheap and run
# once, only the solid checks are paged. Merging them would page cheap work and
# leave the expensive part unbounded.
foreach ($fragment in @(
  'public static ClashSession Begin(',
  'public ClashPageResult RunPage(',
  'public void ReleaseGeometryCaches()',
  'public bool MatchesModel('
)) { Assert-Contains $session $fragment "Chunked clash session is missing: $fragment" }

# A page must stop on all three limits. A result-only limit never terminates on
# a clean model, where hundreds of thousands of candidates yield a handful of
# clashes; a time-only limit makes page size vary run to run.
foreach ($fragment in @(
  'ClashPageStopReason.ResultCapReached',
  'ClashPageStopReason.WorkCapReached',
  'ClashPageStopReason.TimeCapReached'
)) { Assert-Contains $session $fragment "Page budget must honour every stop condition: $fragment" }

foreach ($fragment in @(
  'MaxResults',
  'MaxExactChecks',
  'MaxDuration',
  'ForCandidates',
  'MsPerExactCheck'
)) { Assert-Contains $cursor $fragment "Page budget contract is missing: $fragment" }

# Deduplication has to live on the cursor. Inside the sweep it cannot see pairs
# emitted on an earlier page, so a pair reachable from two source combinations
# would be reported twice.
Assert-Contains $cursor 'SeenPairKeys' 'Pair deduplication must persist on the cursor across pages.'
Assert-Contains $session 'Cursor.SeenPairKeys.Add' 'Page loop must deduplicate through the cursor.'

# Spatial grid, not the quadratic skip the sweep used before.
foreach ($fragment in @('ChooseCellSize', 'CellKeys', 'Dictionary<long, List<ElementProxy>>')) {
  Assert-Contains $session $fragment "Spatial grid pairing is missing: $fragment"
}
Assert-NotContains $session 'foreach(var left in a.OrderBy' 'Quadratic bounding-box skip loop must not return.'

# Bounding-box overlap volume bounds the solid intersection volume exactly, so
# it must be tested before geometry is touched.
Assert-Contains $session 'BoxOverlapMm3' 'Bounding-box volume lower bound is missing.'

# Solid caching is right inside a page and wrong across a paged session.
Assert-Contains $proxy 'public bool ReleaseSolids()' 'ElementProxy must expose a releasable solid cache.'
Assert-Contains $session 'ReleaseSolids()' 'Session must release solid caches between pages.'

# Cost counters: without them every constant above is unmeasured guesswork.
foreach ($fragment in @('MsInCollect', 'MsInSweep', 'MsInExact')) {
  Assert-Contains $report $fragment "Clash cost counter is missing: $fragment"
}

# Finalize would hide Object.Finalize.
Assert-Contains $engine 'FinalizeReport' 'Report finalisation must not be named Finalize.'
Assert-NotContains $engine 'public static void Finalize(' 'A static Finalize collides with Object.Finalize.'

# The footer must say results are partial. Creating Issues from a partial set
# without saying so turns a page boundary into a false coordination claim.
foreach ($fragment in @('PARTIAL RESULTS', 'RefreshPagingStatus', 'LoadMoreAsync', 'candidate pairs')) {
  Assert-Contains $ui $fragment "Paging UI contract is missing: $fragment"
}

Write-Host '[OK] Chunked clash detection: two-phase split, cursor, grid, budget and paging UI verified.' -ForegroundColor Green
exit 0
