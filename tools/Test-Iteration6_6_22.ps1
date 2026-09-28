$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$clientPath = Join-Path $root 'src\BuildAI.Core\BuildAiClient.cs'
$source = Get-Content -LiteralPath $clientPath -Raw

$required = @(
  'NormalizeLegacySessionHeartbeatUrl(url)',
  'req.Url = NormalizeLegacySessionHeartbeatUrl(req.Url)',
  'const string legacySuffix = "/heartbeat"',
  'Legacy session heartbeat URL migrated',
  '_queue.Persist()'
)

foreach ($fragment in $required) {
  if ($source.IndexOf($fragment, [System.StringComparison]::Ordinal) -lt 0) {
    throw "6.6.22 legacy heartbeat queue migration is incomplete: $fragment"
  }
}

$transportCall = '_transport.PostJsonAsync(req.Url, req.Json, _cts.Token)'
$normalization = 'req.Url = NormalizeLegacySessionHeartbeatUrl(req.Url)'
if ($source.IndexOf($normalization, [System.StringComparison]::Ordinal) -gt
    $source.IndexOf($transportCall, [System.StringComparison]::Ordinal)) {
  throw 'Legacy heartbeat URL must be normalized before transport.'
}

Write-Host '[OK] 6.6.22 legacy heartbeat queue migration verified.' -ForegroundColor Green
