$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$sessionManager = Join-Path $root 'src\Plugin1.TimeModelAnalytics\Tracking\SessionManager.cs'
$source = Get-Content -LiteralPath $sessionManager -Raw

$expected = '_options.ResolveSessionsUrl().TrimEnd(''/'') + "/" + p.ClientSessionId'
if ($source.IndexOf($expected, [System.StringComparison]::Ordinal) -lt 0) {
  throw "Heartbeat is not addressed to /api/revit/sessions/{client_session_id}."
}

if ($source.IndexOf('p.ClientSessionId+"/heartbeat"', [System.StringComparison]::Ordinal) -ge 0 -or
    $source.IndexOf('p.ClientSessionId + "/heartbeat"', [System.StringComparison]::Ordinal) -ge 0) {
  throw "Unsupported /heartbeat suffix is still used for session heartbeat delivery."
}

if ($source.IndexOf('Session heartbeat queued', [System.StringComparison]::Ordinal) -lt 0) {
  throw "Heartbeat destination diagnostic logging is missing."
}

Write-Host '[OK] 6.6.21 session heartbeat endpoint verified.' -ForegroundColor Green
