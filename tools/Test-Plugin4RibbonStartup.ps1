$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$applicationPath = Join-Path $root "src\Plugin4.LinkComparatorAI\Revit\Application.cs"
$contextPath = Join-Path $root "src\Plugin4.LinkComparatorAI\Revit\PluginContext.cs"
$commandsPath = Join-Path $root "src\Plugin4.LinkComparatorAI\Revit\Commands.cs"

Write-Host "==> Checking fail-open AR-ST ribbon startup"

foreach ($path in @($applicationPath, $contextPath, $commandsPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required Plugin4 startup source is missing: $path"
    }
}

$application = Get-Content -LiteralPath $applicationPath -Raw
$context = Get-Content -LiteralPath $contextPath -Raw
$commands = Get-Content -LiteralPath $commandsPath -Raw

$ribbonIndex = $application.IndexOf('RegisterRibbon(app);', [StringComparison]::Ordinal)
$servicesIndex = $application.IndexOf('PluginContext.EnsureInitialized();', [StringComparison]::Ordinal)
if ($ribbonIndex -lt 0 -or $servicesIndex -lt 0 -or $ribbonIndex -ge $servicesIndex) {
    throw "Plugin4 must register its ribbon before Viewer Probe and ExternalEvent services are initialized."
}

foreach ($required in @(
    '"BuildAI_P4_Compare"',
    '"BuildAI_P4_Rooms"',
    '"BuildAI_P4_Results"',
    '"BuildAI_Settings"',
    'Ribbon remains available and initialization will be retried',
    'return Result.Succeeded;'
)) {
    if ($application -notmatch [regex]::Escape($required)) {
        throw "Plugin4 fail-open ribbon marker is missing: $required"
    }
}

foreach ($required in @(
    'public static void EnsureInitialized()',
    'if (Issues == null)',
    'if (ActionEvent == null) ActionEvent = ExternalEvent.Create(ActionHandler);',
    'InitializationError = null;'
)) {
    if ($context -notmatch [regex]::Escape($required)) {
        throw "Plugin4 retryable initialization marker is missing: $required"
    }
}

$retryCount = [regex]::Matches($commands, [regex]::Escape('PluginContext.EnsureInitialized();')).Count
if ($retryCount -lt 3) {
    throw "Compare, Results, and Settings commands must retry Plugin4 initialization. Found: $retryCount"
}

Write-Host "[OK] AR-ST ribbon is registered first and all commands can retry service initialization."
