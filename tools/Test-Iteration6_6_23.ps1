$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

$sharedPath = Join-Path $root 'src\ExternalEvent.Shared\ExternalEventOperation.cs'
$plugin4HandlerPath = Join-Path $root 'src\Plugin4.LinkComparatorAI\Revit\ComparatorActionHandler.cs'
$plugin5HandlerPath = Join-Path $root 'src\Plugin5.ClashFormaIntegration\Revit\ModelActionHandler.cs'
$plugin4WindowPath = Join-Path $root 'src\Plugin4.LinkComparatorAI\UI\ResultsWindow.cs'
$plugin5WindowPath = Join-Path $root 'src\Plugin5.ClashFormaIntegration\UI\ResultsWindow.cs'

$shared = Get-Content -LiteralPath $sharedPath -Raw
$handlers = (Get-Content -LiteralPath $plugin4HandlerPath -Raw) + "`n" +
            (Get-Content -LiteralPath $plugin5HandlerPath -Raw)
$windows = (Get-Content -LiteralPath $plugin4WindowPath -Raw) + "`n" +
           (Get-Content -LiteralPath $plugin5WindowPath -Raw)

$requiredShared = @(
  'raiseResult = externalEvent.Raise()',
  'EXTERNAL EVENT RAISE',
  'EXTERNAL EVENT START TIMEOUT',
  'EXTERNAL EVENT COMPLETION TIMEOUT',
  # 7.0.7 replaced the single 30-second race with a heartbeat loop, because
  # measured raise-to-Execute delays on large models reached 51 s and the old
  # budget failed healthy runs. The invariant still checked: the start wait is
  # bounded, observable, and resolved against operation.Started.
  'EXTERNAL EVENT WAITING FOR REVIT',
  'winner == operation.Started',
  'DefaultStartTimeout',
  'Task.WhenAny(operation.Completion, Task.Delay(completionTimeout))',
  'TaskCreationOptions.RunContinuationsAsynchronously'
)
foreach ($fragment in $requiredShared) {
  if ($shared.IndexOf($fragment, [System.StringComparison]::Ordinal) -lt 0) {
    throw "6.6.23 ExternalEvent guard is incomplete: $fragment"
  }
}

foreach ($fragment in @('EXTERNAL EVENT EXECUTE ENTER', 'EXTERNAL EVENT EXECUTE EXIT', 'No active Revit document.')) {
  if (($handlers.Split([string[]]@($fragment), [System.StringSplitOptions]::None).Length - 1) -lt 2) {
    throw "Both Issue handlers must log and complete this ExternalEvent stage: $fragment"
  }
}

if (($windows.Split([string[]]@('ExternalEventAwaiter.RaiseAndWaitAsync'), [System.StringSplitOptions]::None).Length - 1) -lt 4) {
  throw 'AR-ST and Clash must guard both preparation and synchronization ExternalEvents.'
}

foreach ($project in @(
  'src\Plugin4.LinkComparatorAI\Plugin4.LinkComparatorAI.csproj',
  'src\Plugin5.ClashFormaIntegration\Plugin5.ClashFormaIntegration.csproj'
)) {
  $projectSource = Get-Content -LiteralPath (Join-Path $root $project) -Raw
  if ($projectSource.IndexOf('ExternalEventOperation.cs', [System.StringComparison]::Ordinal) -lt 0) {
    throw "Shared ExternalEvent guard is not compiled into $project"
  }
}

Write-Host '[OK] 6.6.23 ExternalEvent timeout and diagnostics guard verified.' -ForegroundColor Green
