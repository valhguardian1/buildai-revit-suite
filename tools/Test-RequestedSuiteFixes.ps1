$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

function Read-Source([string]$relativePath) {
  $path = Join-Path $root $relativePath
  if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required source is missing: $relativePath" }
  return [IO.File]::ReadAllText($path)
}
function Require([string]$text,[string]$needle,[string]$message) {
  if ($text.IndexOf($needle,[StringComparison]::Ordinal) -lt 0) { throw "$message Missing: $needle" }
}
function Reject([string]$text,[string]$needle,[string]$message) {
  if ($text.IndexOf($needle,[StringComparison]::Ordinal) -ge 0) { throw "$message Forbidden: $needle" }
}

$location = Read-Source 'src\BuildAI.Core\Issues\IssueLocationDetailsResolver.cs'
foreach ($marker in @('RowLevel','PrimaryElement','SecondaryElement','TitleFallback','Missing','LastIndexOf')) { Require $location $marker 'Location Details resolver contract is incomplete.' }
Reject $location 'BuildAiViewNames.Coordination' 'Viewable name must not be a Location Details fallback.'

$mode = Read-Source 'src\BuildAI.Core\Issues\ChangeCalculationModePolicy.cs'
foreach ($marker in @('WindowInitialization','UserAction','OperationReset','SettingsLoad','CHANGE_CALCULATION_MODE')) { Require $mode $marker 'Change calculation mode policy is incomplete.' }

$merge = Read-Source 'src\BuildAI.Core\Issues\ExistingIssueMergeService.cs'
foreach ($marker in @('AlreadyCreated','PreviouslyCreatedNotDetected','ExactResultKey','EXISTING_ISSUES_MERGE','EXISTING_ISSUE_MATCH')) { Require $merge $marker 'Existing Issue merge contract is incomplete.' }

$model = Read-Source 'src\Plugin5.ClashFormaIntegration\Models\ClashItem.cs'
Require $model 'IssueCreationState CreationState' 'Clash rows do not expose the unified Issue creation state.'
foreach ($marker in @('enum IssueCreationState','AlreadyCreated','PreviouslyCreatedNotDetected','CreationFailed','CreatedThisRun')) { Require $merge $marker 'Issue creation state model is incomplete.' }

$categories = Read-Source 'src\Plugin5.ClashFormaIntegration\Models\MepClashCategories.cs'
Require $categories 'AddHardExcluded("OST_StructuralFraming"' 'Structural Framing is not excluded through BuiltInCategory identity.'
$composer = Read-Source 'src\Plugin5.ClashFormaIntegration\Revit\MepComparisonViewComposer.cs'
foreach ($marker in @('MEP_COMPARISON_VIEW_COMPOSITION','structuralFramingIncluded','SelectedCategoryIds','VisibleResultElements')) { Require $composer $marker 'MEP publication view composition is incomplete.' }

$workflow = Read-Source 'src\Plugin5.ClashFormaIntegration\Issues\ClashIssueWorkflow.cs'
Require $workflow 'IssueLocationDetailsResolver.Resolve' 'Clash payload does not use the level resolver.'
Reject $workflow 'LocationDetails = BuildAiViewNames.Coordination' 'Clash payload still writes the viewable name as Location Details.'
foreach ($marker in @('revitClashesConfirmed','pushpinExactAccepted','pushpinCorrected','cameraFarFallbackAccepted','postAttempted')) { Require $workflow $marker 'Expanded Clash batch diagnostics are incomplete.' }

$node = Get-Command node -ErrorAction SilentlyContinue
$nodePath = if ($node) { $node.Source } else { Join-Path $root '.harness\tools\node\node.exe' }
if (-not (Test-Path -LiteralPath $nodePath)) { throw 'Node.js is required for requested Clash regression tests.' }
& $nodePath (Join-Path $PSScriptRoot 'Test-RequestedClashFixes.cjs')
if ($LASTEXITCODE -ne 0) { throw 'Requested Clash behavioral regression tests failed.' }

dotnet run --project (Join-Path $root 'tests\BuildAI.RequestedFixes.Tests\BuildAI.RequestedFixes.Tests.csproj') --configuration Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Requested C# policy tests failed.' }

Write-Host '[OK] Requested Clash/Issue/MEP regression contracts passed.' -ForegroundColor Green
