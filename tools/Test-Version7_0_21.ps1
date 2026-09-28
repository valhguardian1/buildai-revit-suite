$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

function Read-Utf8([string]$relativePath) {
    $path = Join-Path $root $relativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required 7.0.21 source is missing: $relativePath"
    }
    return Get-Content -LiteralPath $path -Raw
}

function Require([string]$text, [string]$needle, [string]$message) {
    if ($text.IndexOf($needle, [StringComparison]::Ordinal) -lt 0) {
        throw "$message Missing: $needle"
    }
}

function Reject([string]$text, [string]$needle, [string]$message) {
    if ($text.IndexOf($needle, [StringComparison]::Ordinal) -ge 0) {
        throw "$message Forbidden: $needle"
    }
}

Write-Host '==> Checking 7.0.21 Clash view isolation and Issue description limits'

$clashHandler = Read-Utf8 'src\Plugin5.ClashFormaIntegration\Revit\ModelActionHandler.cs'
$clashWindow = Read-Utf8 'src\Plugin5.ClashFormaIntegration\UI\ResultsWindow.cs'
$clashWorkflow = Read-Utf8 'src\Plugin5.ClashFormaIntegration\Issues\ClashIssueWorkflow.cs'
$arStHandler = Read-Utf8 'src\Plugin4.LinkComparatorAI\Revit\ComparatorActionHandler.cs'
$issuesClient = Read-Utf8 'src\BuildAI.Core\Issues\IssueIntegrationClient.cs'
$props = Read-Utf8 'Directory.Build.props'
$wix = Read-Utf8 'installer\BuildAI.RevitSuite.wxs'
$buildInfo = Read-Utf8 'src\BuildAI.Core\BuildInfo.cs'
$buildScript = Read-Utf8 'build.ps1'

foreach ($marker in @(
    'EnsureNamed3D(doc,type,BuildAiViewNames.Coordination)',
    'MepComparisonViewComposer.Compose(doc, coordination, PluginContext.Report)',
    'AR-ST VIEW PRESERVED',
    'BuildAI AR-ST was not modified by the Clash workflow')) {
    Require $clashHandler $marker 'Clash publication isolation is incomplete.'
}

foreach ($forbidden in @(
    'EnsureNamed3D(doc,type,BuildAiViewNames.ArSt)',
    'ResetView(arSt)',
    'CoordinationViewVisibility.EnsureFull(doc, arSt)',
    'SetRelevantLinkVisibility(doc, arSt',
    'HideMepFromArSt(doc, arSt')) {
    Reject $clashHandler $forbidden 'Clash workflow still mutates AR-ST.'
}

Require $arStHandler 'EnsureNamed3D(doc, type, "BuildAI AR-ST")' 'AR-ST workflow no longer owns its publication view.'
Require $arStHandler 'SetSelectedLinkVisibility(doc, arSt' 'AR-ST workflow filtering regressed.'
Require $clashWindow 'BuildAI prepared the Clash 3D view:' 'Clash publication instructions are missing.'
Require $clashWindow 'add BuildAI Coordination to the active set' 'Clash instructions do not identify the required view.'
Reject $clashWindow 'add BOTH views' 'Clash instructions still request the AR-ST view.'

foreach ($marker in @(
    'Description = BuildDescription(row, operationId, generatedAtUtc)',
    'return LimitDescription(string.Join("\n", lines), 1000)',
    'private static string LimitDescription(string value, int maxLength)',
    'Intersection volume: ')) {
    Require $clashWorkflow $marker 'Compact Clash description contract is incomplete.'
}
Reject $clashWorkflow 'Viewer Object Resolution:' 'Verbose Viewer diagnostics leaked into the Issue description.'

foreach ($marker in @(
    'ApplyDescriptionLimit(payload, diagnosticsProgress)',
    'const int maxDescriptionLength = 1000',
    'private static bool IsAssigneeValidationError',
    'Autodesk rejected the Issue request.')) {
    Require $issuesClient $marker 'Core Issue request validation is incomplete.'
}
Reject $issuesClient 'if (!string.IsNullOrWhiteSpace(payload?.AssignedTo))' 'Every validation error is still treated as an assignee failure.'

$versionMatch = [regex]::Match($props, '<Version>(\d+\.\d+(?:\.\d+)?)</Version>')
if (-not $versionMatch.Success) { throw 'Directory.Build.props has no semantic product version.' }
$expectedVersion = $versionMatch.Groups[1].Value
Require $wix "Version=`"$expectedVersion`"" 'MSI version mismatch.'
Require $buildInfo "public const string Version = `"$expectedVersion`";" 'Runtime version mismatch.'
Require $buildScript "BuildAI_RevitSuite_${expectedVersion}_Setup.msi" 'Build output version mismatch.'

Write-Host '[OK] Clash uses only BuildAI Coordination, AR-ST remains owned by Plugin4, and every outgoing Issue description is capped at 1000 characters.' -ForegroundColor Green
