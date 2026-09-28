$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

function Read-Source([string]$relative) {
  [System.IO.File]::ReadAllText((Join-Path $root $relative), [System.Text.Encoding]::UTF8)
}
function Require([string]$text, [string]$fragment, [string]$message) {
  if ($text.IndexOf($fragment, [StringComparison]::Ordinal) -lt 0) { throw $message }
}

$pending = Read-Source 'src\BuildAI.Core\Issues\PendingIssueBatchStore.cs'
$fingerprints = Read-Source 'src\BuildAI.Core\Issues\IssueFingerprintStore.cs'
$viewer = Read-Source 'src\ViewerProbe.Shared\ViewerCoordinateProbe.cs'
$publication = Read-Source 'src\BuildAI.Core\APS\ApsPublicationClient.cs'
$integration = Read-Source 'src\BuildAI.Core\Issues\IssueIntegrationClient.cs'
$logging = Read-Source 'src\BuildAI.Core\Logging\PluginLog.cs'
$build = Read-Source 'build.ps1'
$workflows = @(
  (Read-Source 'src\Plugin5.ClashFormaIntegration\Issues\ClashIssueWorkflow.cs'),
  (Read-Source 'src\Plugin4.LinkComparatorAI\Issues\ComparatorIssueWorkflow.cs')
)

foreach ($fragment in @('pending-buildai-sync.json','File.Replace(temp, _path, backup, true)','GetPending','Enqueue','Acknowledge','InvalidDataException')) {
  Require $pending $fragment "Durable BuildAI outbox invariant missing: $fragment"
}
foreach ($fragment in @('File.Replace(temp, _path, backup, true)','stream.Flush(true)','InvalidDataException')) {
  Require $fingerprints $fragment "Atomic fingerprint invariant missing: $fragment"
}
foreach ($workflow in $workflows) {
  foreach ($fragment in @('GetExistingIssuesAsync(modelUid','pendingStore.GetPending','pendingStore.Enqueue','pendingStore.Acknowledge','queued for BuildAI-only retry')) {
    Require $workflow $fragment "Issue recovery workflow invariant missing: $fragment"
  }
  $enqueueIndex = $workflow.IndexOf('pendingStore.Enqueue', [StringComparison]::Ordinal)
  $createdFingerprintIndex = $workflow.IndexOf('fingerprintStore.MarkCreated', $enqueueIndex, [StringComparison]::Ordinal)
  if ($enqueueIndex -lt 0 -or $createdFingerprintIndex -lt $enqueueIndex) {
    throw 'The durable outbox must be written before the local created fingerprint.'
  }
}
foreach ($fragment in @('out-of-range batch indices','duplicate batch indices','omitted batch indices','Enumerable.Range(0, _requests.Count)')) {
  Require $viewer $fragment "Viewer batch identity validation missing: $fragment"
}
foreach ($fragment in @('rootSuccessWithoutTargetSinceUtc','TimeSpan.FromMinutes(3)','eventual-consistency grace period')) {
  Require $publication $fragment "Manifest convergence guard missing: $fragment"
}
if ($integration.IndexOf('rawUrns.Add(model.DocumentUrn)', [StringComparison]::Ordinal) -ge 0) {
  throw 'Document/item URN must not be probed as a Model Derivative URN.'
}
foreach ($fragment in @('currentDerivativeUrn','environment.ModelUrn = currentDerivativeUrn')) {
  Require $integration $fragment "Current-version regional context invariant missing: $fragment"
}
if ($build.IndexOf('& powershell.exe', [StringComparison]::OrdinalIgnoreCase) -ge 0) {
  throw 'The build pipeline must use the same PowerShell executable that started build.ps1.'
}
foreach ($fragment in @('List<LogEntry> batch = null','Network exceptions happen after the batch has been dequeued','Queue.Enqueue(entry);','File.Replace(temp, SpoolPath, backup, true)','stream.Flush(true)')) {
  Require $logging $fragment "Remote logging recovery invariant missing: $fragment"
}
$writeStart = $logging.IndexOf('private static void Write', [StringComparison]::Ordinal)
$flushStart = $logging.IndexOf('public static async Task FlushAsync', [StringComparison]::Ordinal)
$restoreStart = $logging.IndexOf('private static void RestoreSpool', [StringComparison]::Ordinal)
$writeBlock = $logging.Substring($writeStart, $flushStart - $writeStart)
$flushBlock = $logging.Substring($flushStart, $restoreStart - $flushStart)
if ($writeBlock.IndexOf('batch != null', [StringComparison]::Ordinal) -ge 0) {
  throw 'A dequeued remote-log batch was referenced from Write(), outside its scope.'
}
foreach ($fragment in @('List<LogEntry> batch = null','if (batch != null)','RewriteSpoolFromQueue();')) {
  Require $flushBlock $fragment "FlushAsync batch recovery is incomplete: $fragment"
}

Write-Host '[OK] BuildAI 7.0.4 durable recovery, manifest convergence, regional context and Viewer batch identity verified.' -ForegroundColor Green
