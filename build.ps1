<#
.SYNOPSIS
  Builds the BuildAI Revit plugins, stages a self-contained payload for each
  supported Revit version, creates a single-file MSI, and verifies the MSI.

.USAGE
  pwsh -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
  pwsh -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Configuration Debug

.REQUIREMENTS
  * Visual Studio 2022 / .NET SDK + .NET Framework 4.8 targeting pack
  * PowerShell 7 available as `pwsh`
  * WiX Toolset 5 available as the global `wix` command
  * WiX Toolset MSI decompiler for post-build payload verification
#>
param(
  [string[]]$Versions = @('2023','2024','2025','2026'),
  [ValidateSet('Debug','Release')] [string]$Configuration = 'Release',
  [switch]$Protect,
  [switch]$SkipMsiVerification,
  [string]$SigningPfxPath = '',
  [string]$SigningPfxPassword = ''
)

$ErrorActionPreference = 'Stop'
$root         = $PSScriptRoot
$installerDir = Join-Path $root 'installer'
$brandingCheck = Join-Path $root 'tools\Test-InstallerBranding.ps1'
& $brandingCheck -InstallerDirectory $installerDir -WixSource (Join-Path $installerDir 'BuildAI.RevitSuite.wxs') -InnoSource (Join-Path $installerDir 'BuildAI.iss')
$payloadRoot  = Join-Path $root 'installer\payload'
$supportedVersions = @('2023','2024','2025','2026')

$requestedVersions = @($Versions | ForEach-Object { ([string]$_).Trim() } | Where-Object { $_ } | Select-Object -Unique)
$unsupportedVersions = @($requestedVersions | Where-Object { $_ -notin $supportedVersions })
$missingVersions = @($supportedVersions | Where-Object { $_ -notin $requestedVersions })
if ($requestedVersions.Count -eq 0) {
  throw 'At least one Revit version must be requested.'
}
if ($unsupportedVersions.Count -gt 0) {
  throw "Unsupported Revit version(s): $($unsupportedVersions -join ', '). Supported versions: $($supportedVersions -join ', ')."
}
if ($missingVersions.Count -gt 0) {
  throw "The release MSI must contain every supported Revit payload. Missing version(s): $($missingVersions -join ', '). Run build.ps1 without -Versions."
}
$Versions = $supportedVersions

function Clear-InternetZoneIdentifier([string]$Path) {
  if (-not (Test-Path -LiteralPath $Path)) { return }

  $files = if (Test-Path -LiteralPath $Path -PathType Leaf) {
    @(Get-Item -LiteralPath $Path)
  }
  else {
    @(Get-ChildItem -LiteralPath $Path -File -Recurse -Force -ErrorAction Stop)
  }

  foreach ($file in $files) {
    # Unblock-File removes only the Zone.Identifier alternate data stream. It
    # does not change file contents, hashes or Authenticode signatures.
    Unblock-File -LiteralPath $file.FullName -ErrorAction Stop
  }
}

function Assert-NoInternetZoneIdentifier([string]$Path) {
  if (-not (Test-Path -LiteralPath $Path)) { return }

  $files = if (Test-Path -LiteralPath $Path -PathType Leaf) {
    @(Get-Item -LiteralPath $Path)
  }
  else {
    @(Get-ChildItem -LiteralPath $Path -File -Recurse -Force -ErrorAction Stop)
  }

  $marked = New-Object System.Collections.Generic.List[string]
  foreach ($file in $files) {
    try {
      $zoneStream = Get-Item -LiteralPath $file.FullName -Stream 'Zone.Identifier' -ErrorAction Stop
      if ($null -ne $zoneStream) { $marked.Add($file.FullName) }
    }
    catch {
      # The PowerShell FileSystem provider can reject long restored obj paths
      # when querying alternate streams. .NET handles these paths directly.
      if ([IO.File]::Exists($file.FullName + ':Zone.Identifier')) { $marked.Add($file.FullName) }
    }
  }

  if ($marked.Count -gt 0) {
    throw "Internet-origin metadata remains on packaged files:`n$($marked -join "`n")"
  }
}

$currentPowerShellExe = (Get-Process -Id $PID -ErrorAction Stop).Path

function Invoke-ValidationScript(
  [string]$RelativePath,
  [string]$FailureMessage,
  [string[]]$Arguments = @()
) {
  $scriptPath = Join-Path $root $RelativePath
  if (-not (Test-Path -LiteralPath $scriptPath -PathType Leaf)) {
    throw "Validation script was not found: $scriptPath"
  }

  $global:LASTEXITCODE = 0
  & $currentPowerShellExe -NoLogo -NoProfile -ExecutionPolicy Bypass -File $scriptPath @Arguments
  $validationExitCode = $LASTEXITCODE
  if ($validationExitCode -ne 0) {
    throw "$FailureMessage Exit code: $validationExitCode"
  }
}

Write-Host "==> Checking build prerequisites" -ForegroundColor Cyan
$prerequisiteArguments = @()
if ($SigningPfxPath) { $prerequisiteArguments += '-RequireSigningTools' }
Invoke-ValidationScript 'tools\Test-BuildPrerequisites.ps1' 'Build prerequisite validation failed.' $prerequisiteArguments

# Run before invoking any downloaded validation script or compiler input.
Write-Host "==> Removing internet-origin metadata before compilation" -ForegroundColor Cyan
Clear-InternetZoneIdentifier -Path $root
Assert-NoInternetZoneIdentifier -Path $root

Write-Host "==> Checking for ambiguous Revit/WPF UI types" -ForegroundColor Cyan
Invoke-ValidationScript 'tools\Test-AmbiguousUiTypes.ps1' 'UI type ambiguity check failed.'

Write-Host "==> Checking for duplicate class members" -ForegroundColor Cyan
Invoke-ValidationScript 'tools\Test-DuplicateClassMembers.ps1' 'Duplicate class member check failed.'

Write-Host "==> Checking for obsolete member references" -ForegroundColor Cyan
Invoke-ValidationScript 'tools\Test-ObsoleteMemberReferences.ps1' 'Obsolete member reference check failed.'

Write-Host "==> Checking Revit API compatibility" -ForegroundColor Cyan
Invoke-ValidationScript 'tools\Test-RevitApiCompatibility.ps1' 'Revit API compatibility check failed.'

Write-Host "==> Checking native APS publication architecture" -ForegroundColor Cyan
foreach ($validationScript in @(
  'tools\Test-NativeApsPublication.ps1',
  'tools\Test-ApsViewableIdentity.ps1',
  'tools\Test-TargetViewableReadiness.ps1',
  'tools\Test-Iteration6_6_19.ps1',
  'tools\Test-Iteration6_6_20.ps1',
  'tools\Test-Iteration6_6_21.ps1',
  'tools\Test-Iteration6_6_22.ps1',
  'tools\Test-Iteration6_6_23.ps1',
  'tools\Test-Iteration6_6_24.ps1',
  'tools\Test-MultiRegionDerivativeRouting.ps1',
  'tools\Test-ApsObjectIdentity.ps1',
  'tools\Test-ViewerCoordinateProbe.ps1'
)) {
  Invoke-ValidationScript $validationScript "Validation failed: $validationScript"
}

Write-Host "==> Checking AEC Model Data eventual-consistency recovery" -ForegroundColor Cyan
Invoke-ValidationScript 'tools\Test-AecModelDataReadiness.ps1' 'AEC Model Data readiness validation failed.'
Invoke-ValidationScript 'tools\Test-AecModelDataFullBudget.ps1' 'AEC Model Data full-budget validation failed.'

Write-Host "==> Checking APS transient-network retry recovery" -ForegroundColor Cyan
Invoke-ValidationScript 'tools\Test-ApsTransientNetworkRetry.ps1' 'APS transient-network retry validation failed.'

Write-Host "==> Checking large-model Issue resolution" -ForegroundColor Cyan
Invoke-ValidationScript 'tools\Test-LargeModelIssueResolution.ps1' 'Large-model Issue resolution validation failed.'

Write-Host "==> Checking multi-row Issue checkbox selection" -ForegroundColor Cyan
Invoke-ValidationScript 'tools\Test-IssueCheckboxSelection.ps1' 'Issue checkbox selection validation failed.'

Write-Host "==> Checking AR-ST ribbon startup recovery" -ForegroundColor Cyan
Invoke-ValidationScript 'tools\Test-Plugin4RibbonStartup.ps1' 'AR-ST ribbon startup recovery validation failed.'

Write-Host "==> Checking AR-ST selected-link refresh" -ForegroundColor Cyan
Invoke-ValidationScript 'tools\Test-ArStLinkRefresh.ps1' 'AR-ST selected-link refresh validation failed.'

Write-Host "==> Checking IProgress compatibility across all plugins" -ForegroundColor Cyan
Invoke-ValidationScript 'tools\Test-ProgressReportCompatibility.ps1' 'IProgress compatibility check failed.'

Write-Host "==> Checking user-facing language purity" -ForegroundColor Cyan
Invoke-ValidationScript 'tools\Test-LanguagePurity.ps1' 'User-facing language purity validation failed.'

Write-Host "==> Checking Fix6 publication, Issue fields and room heights" -ForegroundColor Cyan
Invoke-ValidationScript 'tools\Test-FunctionalFix6.ps1' 'Fix6 functional source validation failed.'
Invoke-ValidationScript 'tools\Test-RecalculateAccPublication.ps1' 'Recalculate ACC publication validation failed.'
Invoke-ValidationScript 'tools\Test-AccCloudModelUrn.ps1' 'Recalculate cloud model URN validation failed.'

Write-Host "==> Checking 6.6.24 safe enhancements and protected baseline" -ForegroundColor Cyan
Invoke-ValidationScript 'tools\Test-SafeEnhancements.ps1' '6.6.24 safe enhancement validation failed.'

Write-Host "==> Checking 7.0.3 coordination visibility and existing safeguards" -ForegroundColor Cyan
Invoke-ValidationScript 'tools\Test-Version7_0_3.ps1' '7.0.3 source regression validation failed.'

Write-Host "==> Checking Viewer anchor probe" -ForegroundColor Cyan
Invoke-ValidationScript 'tools\Test-AnchorProbe.ps1' 'Anchor probe validation failed.'

Write-Host "==> Checking ACC Issue camera focus contract" -ForegroundColor Cyan
Invoke-ValidationScript 'tools\Test-CameraFocusContract.ps1' 'ACC Issue camera focus validation failed.'
Invoke-ValidationScript 'tools\Test-RequestedSuiteFixes.ps1' 'Requested Clash/Issue/MEP regression validation failed.'
Invoke-ValidationScript 'tools\Test-IssueNavigator.ps1' 'Removed ACC reverse import validation failed.'

Write-Host "==> Checking 7.0.4 safe flow recovery" -ForegroundColor Cyan
Invoke-ValidationScript 'tools\Test-SafeFlowRecovery7_0_4.ps1' '7.0.4 safe flow recovery validation failed.'

Write-Host "==> Checking surface-aware AR-ST PushPins" -ForegroundColor Cyan
Invoke-ValidationScript 'tools\Test-SurfaceAwareArStPushpins.ps1' 'Surface-aware AR-ST PushPin validation failed.'

Write-Host "==> Checking 7.0.15 visible PushPins, Viewer batch recovery and one-command build" -ForegroundColor Cyan
Invoke-ValidationScript 'tools\Test-Version7_0_15.ps1' '7.0.15 release validation failed.'

Write-Host "==> Checking restored Issue highlight and compact LLM diagnostics" -ForegroundColor Cyan
Invoke-ValidationScript 'tools\Test-HighlightAndCompactLlmLog.ps1' 'Issue highlight / compact LLM log validation failed.'
Invoke-ValidationScript 'tools\Test-Version7_0_21.ps1' '7.0.21 Clash view isolation / Issue description validation failed.'
Invoke-ValidationScript 'tools\Test-Version7_1_0.ps1' '7.1.0 dual Clash highlight validation failed.'

Write-Host "==> Checking chunked clash detection" -ForegroundColor Cyan
Invoke-ValidationScript 'tools\Test-ChunkedClash.ps1' 'Chunked clash detection validation failed.'

$plugins = @(
  @{
    Csproj   = 'src\Plugin1.TimeModelAnalytics\Plugin1.TimeModelAnalytics.csproj'
    Dll      = 'BuildAI.Plugin1.TimeModelAnalytics.dll'
    Addin    = 'BuildAI.Plugin1.addin'
    Name     = 'BuildAI - Time &amp; Model Analytics'
    AddinId  = '3F9C1A20-7B5E-4D2A-9C11-0A1B2C3D4E51'
    Class    = 'Plugin1.TimeModelAnalytics.Revit.Application'
  },
  @{
    Csproj   = 'src\Plugin2.VolumeEstimator\Plugin2.VolumeEstimator.csproj'
    Dll      = 'BuildAI.Plugin2.VolumeEstimator.dll'
    Addin    = 'BuildAI.Plugin2.addin'
    Name     = 'BuildAI - Volume Estimator'
    AddinId  = '4A8D2B31-6C7F-4E1B-8D22-1B2C3D4E5F62'
    Class    = 'Plugin2.VolumeEstimator.Revit.Application'
  },
  @{
    Csproj   = 'src\Plugin3.LinkChangeMonitor\Plugin3.LinkChangeMonitor.csproj'
    Dll      = 'BuildAI.Plugin3.LinkChangeMonitor.dll'
    Addin    = 'BuildAI.Plugin3.addin'
    Name     = 'BuildAI - Link Change Monitor'
    AddinId  = '5B9E3C42-7D8A-4F2C-9E33-2C3D4E5F6073'
    Class    = 'Plugin3.LinkChangeMonitor.Revit.Application'
  },
  @{
    Csproj   = 'src\Plugin4.LinkComparatorAI\Plugin4.LinkComparatorAI.csproj'
    Dll      = 'BuildAI.Plugin4.LinkComparatorAI.dll'
    Addin    = 'BuildAI.Plugin4.addin'
    Name     = 'BuildAI - Link Comparator + AI'
    AddinId  = '6CA04D53-8E9B-403D-AF44-3D4E5F607184'
    Class    = 'Plugin4.LinkComparatorAI.Revit.Application'
  },
  @{
    Csproj   = 'src\Plugin5.ClashFormaIntegration\Plugin5.ClashFormaIntegration.csproj'
    Dll      = 'BuildAI.Plugin5.ClashFormaIntegration.dll'
    Addin    = 'BuildAI.Plugin5.addin'
    Name     = 'BuildAI - Clash + Forma Integration'
    AddinId  = '7DB15E64-9FAC-414E-B055-4E5F607192A5'
    Class    = 'Plugin5.ClashFormaIntegration.Revit.Application'
  }
)

$map = @{
  '2023' = @{ Cfg = "$Configuration R23"; Tfm = 'net48' }
  '2024' = @{ Cfg = "$Configuration R24"; Tfm = 'net48' }
  '2025' = @{ Cfg = "$Configuration R25"; Tfm = 'net8.0-windows' }
  '2026' = @{ Cfg = "$Configuration R26"; Tfm = 'net8.0-windows' }
}

function New-Addin([string]$Path, [string]$Name, [string]$AddinId, [string]$Class, [string]$Dll) {
@"
<?xml version="1.0" encoding="utf-8"?>
<!-- BuildAI release 7.3.31 -->
<RevitAddIns>
  <AddIn Type="Application">
    <Name>$Name</Name>
    <Assembly>$Dll</Assembly>
    <AddInId>$AddinId</AddInId>
    <FullClassName>$Class</FullClassName>
    <VendorId>BUILDAI</VendorId>
    <VendorDescription>BuildAI, https://app.buildai.me</VendorDescription>
  </AddIn>
</RevitAddIns>
"@ | Set-Content -Path $Path -Encoding UTF8
}

function Copy-RuntimeOutput([string]$Bin, [string]$Destination) {
  # Keep every managed/runtime dependency produced by dotnet build. Revit's own
  # API assemblies must never be redistributed with the add-in.
  #
  # Some SDK/library builds do not emit optional files such as *.deps.json.
  # Materialize the file list and re-check every path immediately before copying
  # so a missing optional artifact cannot abort the complete multi-version build.
  $runtimeFiles = @(Get-ChildItem -Path $Bin -File -ErrorAction Stop | Where-Object {
    $_.Extension -in @('.dll', '.json') -and
    $_.Name -notin @('RevitAPI.dll', 'RevitAPIUI.dll')
  })

  foreach ($file in $runtimeFiles) {
    $source = $file.FullName
    $target = Join-Path $Destination $file.Name

    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
      Write-Warning "Optional runtime artifact disappeared before staging and was skipped: $source"
      continue
    }

    try {
      Copy-Item -LiteralPath $source -Destination $target -Force -ErrorAction Stop
    }
    catch [System.Management.Automation.ItemNotFoundException] {
      Write-Warning "Optional runtime artifact was not produced and was skipped: $source"
    }
  }

  # Copy satellite resources and packaged UI resources while excluding build
  # metadata directories that Revit does not need.
  $resourceDirectories = @(Get-ChildItem -Path $Bin -Directory -ErrorAction Stop | Where-Object {
    $_.Name -notin @('ref', 'refint', 'runtimes')
  })

  foreach ($directory in $resourceDirectories) {
    if (-not (Test-Path -LiteralPath $directory.FullName -PathType Container)) {
      Write-Warning "Optional resource directory disappeared before staging and was skipped: $($directory.FullName)"
      continue
    }

    $target = Join-Path $Destination $directory.Name
    if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
    Copy-Item -LiteralPath $directory.FullName -Destination $target -Recurse -Force -ErrorAction Stop
  }

  # Microsoft.Web.WebView2 places the architecture-specific native loader in
  # runtimes\win-x64\native for some target frameworks. Revit loads add-ins
  # from a flat version folder, so stage the x64 loader beside the managed DLLs.
  $webViewLoaderCandidates = @(
    (Join-Path $Bin 'WebView2Loader.dll'),
    (Join-Path $Bin 'runtimes\win-x64\native\WebView2Loader.dll')
  )
  $webViewLoader = $webViewLoaderCandidates | Where-Object {
    Test-Path -LiteralPath $_ -PathType Leaf
  } | Select-Object -First 1
  if ($webViewLoader) {
    Copy-Item -LiteralPath $webViewLoader -Destination (Join-Path $Destination 'WebView2Loader.dll') -Force
  }

  # Assemblies are mandatory. Optional SDK metadata is not.
  $stagedDlls = @(Get-ChildItem -Path $Destination -Filter '*.dll' -File -ErrorAction SilentlyContinue)
  Write-Host ("    runtime staged: {0} DLL(s), {1} JSON file(s)" -f     $stagedDlls.Count,     @(Get-ChildItem -Path $Destination -Filter '*.json' -File -ErrorAction SilentlyContinue).Count) -ForegroundColor DarkGray
}

function Assert-Payload([string]$Version, [string]$Destination) {
  $missing = New-Object System.Collections.Generic.List[string]
  foreach ($p in $plugins) {
    foreach ($name in @($p.Dll, $p.Addin)) {
      $path = Join-Path $Destination $name
      if (-not (Test-Path $path -PathType Leaf)) { $missing.Add($path) }
    }

    $manifest = Join-Path $Destination $p.Addin
    if (Test-Path $manifest) {
      $xmlText = Get-Content $manifest -Raw
      if ($xmlText -notmatch [regex]::Escape("<Assembly>$($p.Dll)</Assembly>")) {
        throw "Invalid Assembly path in $manifest. DLL and .addin must be in the same folder."
      }
    }
  }

  if (-not (Test-Path (Join-Path $Destination 'BuildAI.Core.dll'))) {
    $missing.Add((Join-Path $Destination 'BuildAI.Core.dll'))
  }

  foreach ($webViewFile in @(
    'Microsoft.Web.WebView2.Core.dll',
    'Microsoft.Web.WebView2.Wpf.dll',
    'WebView2Loader.dll',
    'ViewerProbe\viewer-probe.html',
    'ViewerProbe\buildai-anchor-probe.js'
  )) {
    $webViewPath = Join-Path $Destination $webViewFile
    if (-not (Test-Path -LiteralPath $webViewPath -PathType Leaf)) { $missing.Add($webViewPath) }
  }

  if ($missing.Count -gt 0) {
    throw "Payload verification failed for Revit $Version. Missing:`n$($missing -join "`n")"
  }

  $files = @(Get-ChildItem $Destination -File -Recurse)
  $bytes = ($files | Measure-Object Length -Sum).Sum
  Write-Host ("    payload verified: {0} files / {1:N0} bytes" -f $files.Count, $bytes) -ForegroundColor Green
}


function Get-ExtendedLengthPath(
  [string]$Path
) {
  $fullPath = [System.IO.Path]::GetFullPath($Path)

  if ($fullPath.StartsWith('\\?\')) { return $fullPath }
  if ($fullPath.StartsWith('\\')) {
    return '\\?\UNC\' + $fullPath.TrimStart('\')
  }

  return '\\?\' + $fullPath
}

function Test-DirectoryLongPath(
  [string]$Path
) {
  if (-not [IO.Path]::GetFullPath($Path).StartsWith([IO.Path]::GetFullPath($root) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw "Cleanup path outside workspace: $Path" }
  $extendedPath = Get-ExtendedLengthPath -Path $Path
  return [System.IO.Directory]::Exists($extendedPath)
}

function Remove-DirectoryReliably(
  [string]$Path
) {
  if (-not [IO.Path]::GetFullPath($Path).StartsWith([IO.Path]::GetFullPath($root) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw "Cleanup path outside workspace: $Path" }
  $extendedPath = Get-ExtendedLengthPath -Path $Path
  if (-not [System.IO.Directory]::Exists($extendedPath)) { return }

  for ($cleanupAttempt = 1; $cleanupAttempt -le 3; $cleanupAttempt++) {
    # Use the Win32 extended-length path from the first attempt. Windows
    # PowerShell 5.1 Remove-Item can fail after partially deleting an obj tree
    # when a generated filename pushes the absolute path beyond MAX_PATH.
    Remove-Item -LiteralPath $extendedPath -Recurse -Force -ErrorAction SilentlyContinue
    if (-not [System.IO.Directory]::Exists($extendedPath)) { return }

    try {
      [System.IO.Directory]::Delete($extendedPath, $true)
      if (-not [System.IO.Directory]::Exists($extendedPath)) { return }
    }
    catch {
      if (-not [System.IO.Directory]::Exists($extendedPath)) { return }
      Write-Warning "Cleanup attempt $cleanupAttempt failed for ${Path}: $($_.Exception.Message)"
    }

    Start-Sleep -Milliseconds (200 * $cleanupAttempt)
  }

  if ([System.IO.Directory]::Exists($extendedPath)) {
    throw "Could not remove recovery directory after retries: $Path"
  }
}

function Invoke-ReliableDotNetBuild(
  [string]$Csproj,
  [string]$BuildConfiguration,
  [string]$ExpectedBin,
  [string]$ExpectedFile
) {
  $projectDirectory = Split-Path $Csproj -Parent
  $projectBin = Join-Path $projectDirectory 'bin'
  $projectObj = Join-Path $projectDirectory 'obj'

  for ($attempt = 1; $attempt -le 2; $attempt++) {
    if ($attempt -eq 1) {
      Write-Host "    build attempt 1/2 (isolated MSBuild, compiler server disabled)" -ForegroundColor DarkGray
      $buildArguments = @(
        'build', $Csproj,
        '-c', $BuildConfiguration,
        '--no-incremental',
        '--disable-build-servers',
        '-m:1',
        '-p:UseSharedCompilation=false',
        '-p:BuildInParallel=false'
      )
    }
    else {
      Write-Warning "Initial build did not produce the expected assembly. Performing a full project recovery rebuild."

      # MSB3030 can occur when MSBuild/NuGet/compiler-server state says that
      # CoreCompile is up to date although the intermediate assembly under obj
      # is absent. Shut down all build servers and remove the complete project
      # output, not only the active configuration folder.
      & dotnet build-server shutdown | Out-Host

      foreach ($path in @($projectBin, $projectObj)) {
        if (Test-Path -LiteralPath $path) {
          Write-Host "    recovery cleanup: $path" -ForegroundColor DarkGray
          Remove-DirectoryReliably -Path $path
        }
      }

      Write-Host "    restoring project before recovery build" -ForegroundColor DarkGray
      & dotnet restore $Csproj -p:Configuration=$BuildConfiguration --disable-parallel
      if ($LASTEXITCODE -ne 0) {
        throw "dotnet restore failed during recovery: $Csproj"
      }

      $buildArguments = @(
        'build', $Csproj,
        '-c', $BuildConfiguration,
        '--no-restore',
        '--no-incremental',
        '--disable-build-servers',
        '-m:1',
        '-p:UseSharedCompilation=false',
        '-p:BuildInParallel=false'
      )
    }

    & dotnet @buildArguments
    $buildExitCode = $LASTEXITCODE
    $expectedAssembly = Join-Path $ExpectedBin $ExpectedFile

    if ($buildExitCode -eq 0 -and (Test-Path -LiteralPath $expectedAssembly -PathType Leaf)) {
      return
    }

    if ($buildExitCode -eq 0) {
      Write-Warning "dotnet build returned success, but the expected assembly is missing: $expectedAssembly"
    }
    else {
      Write-Warning "dotnet build attempt $attempt failed with exit code ${buildExitCode}: $Csproj"
    }
  }

  $expectedAssembly = Join-Path $ExpectedBin $ExpectedFile
  $foundFiles = @()
  if (Test-Path -LiteralPath $ExpectedBin -PathType Container) {
    $foundFiles = @(Get-ChildItem -LiteralPath $ExpectedBin -File -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name)
  }

  throw ("Build recovery failed. Expected assembly was not produced: {0}`nOutput directory: {1}`nFiles found:`n{2}" -f $expectedAssembly, $ExpectedBin, ($foundFiles -join "`n"))
}

if (Test-Path $payloadRoot) { Remove-Item $payloadRoot -Recurse -Force }
New-Item -ItemType Directory -Force -Path $payloadRoot | Out-Null

foreach ($v in $Versions) {
  $cfg = $map[$v].Cfg
  $tfm = $map[$v].Tfm
  Write-Host "==> Building Revit $v  ($cfg / $tfm)" -ForegroundColor Cyan

  $dest = Join-Path $payloadRoot $v
  New-Item -ItemType Directory -Force -Path $dest | Out-Null

  # Build the complete solution once per Revit version. This avoids rebuilding
  # BuildAI.Core five times and cuts the normal build from 20 project builds to 4.
  $solution = Join-Path $root 'BuildAI.RevitSuite.sln'
  Write-Host "    building solution once for $cfg" -ForegroundColor DarkGray
  & dotnet build $solution -c $cfg --no-incremental --disable-build-servers -m:1 -p:UseSharedCompilation=false -p:BuildInParallel=false
  if ($LASTEXITCODE -ne 0) {
    Write-Warning "Solution build failed. Cleaning generated output and retrying once."
    & dotnet build-server shutdown | Out-Null
    Get-ChildItem (Join-Path $root 'src') -Directory | ForEach-Object {
      Remove-DirectoryReliably (Join-Path $_.FullName 'bin')
      Remove-DirectoryReliably (Join-Path $_.FullName 'obj')
    }
    & dotnet restore $solution -p:Configuration=$cfg --disable-parallel
    if ($LASTEXITCODE -ne 0) { throw "Solution restore failed: $cfg" }
    & dotnet build $solution -c $cfg --no-restore --no-incremental --disable-build-servers -m:1 -p:UseSharedCompilation=false -p:BuildInParallel=false
    if ($LASTEXITCODE -ne 0) { throw "Solution build failed after recovery: $cfg" }
  }

  foreach ($plugin in $plugins) {
    $projDir = Split-Path (Join-Path $root $plugin.Csproj) -Parent
    $bin = Join-Path $projDir "bin\$cfg\$tfm"
    $expectedDll = Join-Path $bin $plugin.Dll
    if (-not (Test-Path -LiteralPath $expectedDll -PathType Leaf)) {
      throw "Expected plugin assembly was not produced: $expectedDll"
    }
    Copy-RuntimeOutput $bin $dest
    New-Addin (Join-Path $dest $plugin.Addin) $plugin.Name $plugin.AddinId $plugin.Class $plugin.Dll
  }

  Clear-InternetZoneIdentifier -Path $dest
  Assert-NoInternetZoneIdentifier -Path $dest
  Assert-Payload $v $dest
  Write-Host "    staged -> $dest" -ForegroundColor Green
}

if (-not $Protect) {
  Write-Host "==> Functional test build: Obfuscar protection is disabled" -ForegroundColor Yellow
}

if ($Protect) {
  Write-Host "==> Protecting BuildAI assemblies with pinned Obfuscar profile" -ForegroundColor Cyan
  $protectionScript = Join-Path $root 'tools\Protect-BuildAI-Payload.ps1'
  try {
    # Invoke in the current PowerShell process so string[] remains one named
    # parameter value. A child powershell.exe -File call expands later years
    # into positional arguments (for example, an unexpected bare "2024").
    & $protectionScript -PayloadRoot $payloadRoot -Versions $Versions
  }
  catch {
    throw "BuildAI payload protection failed: $($_.Exception.Message)"
  }
  foreach ($v in $Versions) {
    if ($map.ContainsKey($v)) { Assert-Payload $v (Join-Path $payloadRoot $v) }
  }
}

# The MSI uses safe MajorUpgrade and contains no executable custom actions.
# The legacy old-version prompt was unused by WiX, slowed every build, and was
# the source of repeated MSB3030 failures, so it is intentionally not built.

# Optional Authenticode signing. A trusted Code Signing certificate removes the
# Revit "Unknown Publisher" warning. A self-signed certificate only works on
# machines where that certificate is explicitly trusted.
if ($SigningPfxPath) {
  Write-Host "==> Signing staged DLL/EXE payload" -ForegroundColor Cyan
  & $currentPowerShellExe -NoLogo -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tools\Sign-BuildArtifacts.ps1') `
      -Path $payloadRoot -PfxPath $SigningPfxPath -PfxPassword $SigningPfxPassword
  if ($LASTEXITCODE -ne 0) { throw "Payload signing failed." }
}

# --- Build a single-file MSI with WiX Toolset --------------------------------
$outputDir = Join-Path $root 'installer\output'
if (Test-Path $outputDir) { Remove-Item $outputDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $outputDir | Out-Null

$wix = Get-Command wix -ErrorAction SilentlyContinue
if (-not $wix) {
  throw "WiX was not found. Install WiX 5 with: dotnet tool install --global wix --version 5.*"
}

$wxs = Join-Path $root 'installer\BuildAI.RevitSuite.wxs'
$msi = Join-Path $outputDir 'BuildAI_RevitSuite_7.4_Setup.msi'
Write-Host "==> Building single-file MSI with WiX" -ForegroundColor Cyan
Push-Location (Join-Path $root 'installer')
try {
  & wix build $wxs -arch x64 -o $msi
  if ($LASTEXITCODE -ne 0) { throw "WiX build failed with exit code $LASTEXITCODE" }
}
finally { Pop-Location }

if (-not (Test-Path $msi)) { throw "MSI was not created: $msi" }
if ((Get-Item $msi).Length -lt 250KB) { throw "MSI is unexpectedly small: $((Get-Item $msi).Length) bytes" }
Clear-InternetZoneIdentifier -Path $msi
Assert-NoInternetZoneIdentifier -Path $msi

# --- Verify the actual MSI File table with the same pinned WiX tool ------------
# Do not use WindowsInstaller.Installer.OpenDatabase here. Its COM automation
# signature is marshalled differently by Windows PowerShell 5.1 and PowerShell
# 7. Do not use msiexec /a either: administrative installation applies feature
# selection and can legitimately omit payloads for Revit versions not installed
# on the build machine. WiX decompilation reads the already-built MSI database
# without executing installer feature conditions.
if (-not $SkipMsiVerification) {
  Write-Host "==> Verifying embedded MSI payload" -ForegroundColor Cyan
  $verifyRoot = Join-Path $outputDir '_msi_verify'
  if (Test-Path $verifyRoot) { Remove-Item $verifyRoot -Recurse -Force }
  New-Item -ItemType Directory -Force -Path $verifyRoot | Out-Null
  $decompiledWxs = Join-Path $verifyRoot 'decompiled.wxs'
  $extractedRoot = Join-Path $verifyRoot 'extracted'
  & wix msi decompile $msi -o $decompiledWxs -x $extractedRoot
  if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $decompiledWxs -PathType Leaf)) {
    throw "WiX could not decompile the built MSI for payload verification. Exit code: $LASTEXITCODE"
  }

  [xml]$decompiled = Get-Content -LiteralPath $decompiledWxs -Raw
  $fileNodes = @($decompiled.SelectNodes("//*[local-name()='File']"))
  $expectedFiles = @($Versions | ForEach-Object {
    Get-ChildItem -LiteralPath (Join-Path $payloadRoot $_) -File -Recurse -ErrorAction Stop
  })
  if ($fileNodes.Count -ne $expectedFiles.Count) {
    throw "MSI File table count mismatch. Expected $($expectedFiles.Count) staged files, found $($fileNodes.Count) MSI file entries."
  }

  $expectedNames = @{}
  foreach ($file in $expectedFiles) {
    if (-not $expectedNames.ContainsKey($file.Name)) { $expectedNames[$file.Name] = 0 }
    $expectedNames[$file.Name]++
  }
  $actualNames = @{}
  foreach ($node in $fileNodes) {
    $fileName = $node.GetAttribute('Name')
    if ([string]::IsNullOrWhiteSpace($fileName)) {
      $fileName = [System.IO.Path]::GetFileName($node.GetAttribute('Source'))
    }
    if ($fileName.Contains('|')) { $fileName = $fileName.Split('|')[-1] }
    if ([string]::IsNullOrWhiteSpace($fileName)) { continue }
    if (-not $actualNames.ContainsKey($fileName)) { $actualNames[$fileName] = 0 }
    $actualNames[$fileName]++
  }
  $mismatches = New-Object System.Collections.Generic.List[string]
  foreach ($entry in $expectedNames.GetEnumerator() | Sort-Object Name) {
    $actualCount = if ($actualNames.ContainsKey($entry.Name)) { [int]$actualNames[$entry.Name] } else { 0 }
    if ($actualCount -ne [int]$entry.Value) {
      $mismatches.Add("$($entry.Name): staged=$($entry.Value), MSI=$actualCount")
    }
  }
  if ($mismatches.Count -gt 0) {
    throw "MSI payload name/count verification failed:`n$($mismatches -join "`n")"
  }

  # Inspect cabinet bytes, not just MSI File table rows. WiX exports each
  # embedded file under File/<FileId>; resolve its year and subfolder from the
  # decompiled directory tree and compare it with the staged Release payload.
  $embeddedProblems = New-Object System.Collections.Generic.List[string]
  [xml]$versionProps = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw
  $expectedVersion = [string]$versionProps.Project.PropertyGroup.Version[0]
  foreach ($node in $fileNodes) {
    $segments = New-Object System.Collections.Generic.List[string]
    $year = $null
    $ancestor = $node.ParentNode
    while ($null -ne $ancestor) {
      if ($ancestor.LocalName -eq 'Directory') {
        $directoryName = $ancestor.GetAttribute('Name')
        if ($directoryName -in $Versions) { $year = $directoryName; break }
        if (-not [string]::IsNullOrWhiteSpace($directoryName)) { $segments.Insert(0, $directoryName) }
      }
      $ancestor = $ancestor.ParentNode
    }
    if (-not $year) { $embeddedProblems.Add("File outside a supported Revit year: $($node.GetAttribute('Id'))"); continue }
    $name = $node.GetAttribute('Name')
    $stagedPath = Join-Path $payloadRoot $year
    foreach ($segment in $segments) { $stagedPath = Join-Path $stagedPath $segment }
    $stagedPath = Join-Path $stagedPath $name
    $embeddedPath = Join-Path (Join-Path $extractedRoot 'File') $node.GetAttribute('Id')
    if (-not (Test-Path -LiteralPath $stagedPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $embeddedPath -PathType Leaf)) {
      $embeddedProblems.Add("Missing staged or embedded file: Revit $year / $name")
      continue
    }
    $stagedHash = (Get-FileHash -LiteralPath $stagedPath -Algorithm SHA256).Hash
    $embeddedHash = (Get-FileHash -LiteralPath $embeddedPath -Algorithm SHA256).Hash
    if ($stagedHash -ne $embeddedHash) {
      $embeddedProblems.Add("Cabinet hash differs from Release payload: Revit $year / $name")
    }
    if ($name -like 'BuildAI.*.dll' -and $segments.Count -eq 0) {
      $fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($embeddedPath).FileVersion
      if ($fileVersion -notlike "$expectedVersion.*") {
        $embeddedProblems.Add("Embedded DLL version $fileVersion differs from $expectedVersion : Revit $year / $name")
      }
    }
  }
  if ($embeddedProblems.Count -gt 0) {
    throw "MSI embedded payload verification failed:`n$($embeddedProblems -join "`n")"
  }
  Write-Host ("    embedded MSI bytes and BuildAI DLL versions verified: {0} files" -f $fileNodes.Count) -ForegroundColor Green

  $package = $decompiled.SelectSingleNode("//*[local-name()='Package']")
  $upgrade = $decompiled.SelectSingleNode("//*[local-name()='MajorUpgrade']")
  if ($package.GetAttribute('Version') -ne $expectedVersion -or
      $package.GetAttribute('UpgradeCode') -ne '{D20A75E8-CA27-4CD9-9713-4E9EC61EB001}' -or
      $null -eq $upgrade -or
      [string]::IsNullOrWhiteSpace($upgrade.GetAttribute('DowngradeErrorMessage'))) {
    throw 'MSI product version, stable UpgradeCode, major upgrade or downgrade rejection is missing.'
  }

  Write-Host ("    MSI File table verified: {0} files" -f $fileNodes.Count) -ForegroundColor Green

  # --- Verify FEATURE STATE, not just the File table -------------------------
  # An MSI can embed every file and still install nothing. Earlier builds did
  # exactly that: all four features carried Level="0" with a condition that
  # never evaluated true, so setup completed "successfully" and copied nothing.
  # The File table check above passes in that situation, which is why the defect
  # shipped repeatedly. These assertions close that blind spot.
  Write-Host "==> Verifying MSI feature state" -ForegroundColor Cyan
  $featureNodes = @($decompiled.SelectNodes("//*[local-name()='Feature']"))
  if ($featureNodes.Count -eq 0) {
    throw "MSI contains no Feature entries. Nothing would be installed."
  }

  $featureProblems = New-Object System.Collections.Generic.List[string]
  foreach ($feature in $featureNodes) {
    $featureId = $feature.GetAttribute('Id')
    $levelText = $feature.GetAttribute('Level')
    $level = 0
    if (-not [int]::TryParse($levelText, [ref]$level)) {
      $featureProblems.Add("$featureId : Level is '$levelText', which is not a number.")
      continue
    }
    if ($level -le 0) {
      $featureProblems.Add("$featureId : Level=$level. A feature at level 0 is never installed.")
    }
    # A Condition child can drive the level back to 0 at install time. Any such
    # condition must be reviewed deliberately rather than trusted silently.
    $conditions = @($feature.SelectNodes("*[local-name()='Condition']"))
    foreach ($condition in $conditions) {
      $conditionLevel = 0
      [void][int]::TryParse($condition.GetAttribute('Level'), [ref]$conditionLevel)
      if ($conditionLevel -le 0) {
        $featureProblems.Add("$featureId : a Condition can set Level=$conditionLevel, disabling the feature at install time.")
      }
    }
  }
  if ($featureProblems.Count -gt 0) {
    throw "MSI feature state verification failed:`n$($featureProblems -join "`n")"
  }
  Write-Host ("    feature state verified: {0} features, all installable" -f $featureNodes.Count) -ForegroundColor Green

  # --- Verify every component is reachable from a feature --------------------
  # A component that no feature references is dead weight in the MSI: its files
  # ship inside the cabinet but are never written to disk.
  $componentNodes = @($decompiled.SelectNodes("//*[local-name()='Component']"))
  $referencedComponents = New-Object 'System.Collections.Generic.HashSet[string]'
  foreach ($ref in @($decompiled.SelectNodes("//*[local-name()='ComponentRef']"))) {
    [void]$referencedComponents.Add($ref.GetAttribute('Id'))
  }
  # WiX 5 <Files> authoring nests components inside their feature, so a component
  # is reachable either through an explicit ComponentRef or by containment.
  $orphans = New-Object System.Collections.Generic.List[string]
  foreach ($component in $componentNodes) {
    $componentId = $component.GetAttribute('Id')
    if ($referencedComponents.Contains($componentId)) { continue }
    $ancestor = $component.ParentNode
    $nested = $false
    while ($null -ne $ancestor) {
      if ($ancestor.LocalName -eq 'Feature') { $nested = $true; break }
      $ancestor = $ancestor.ParentNode
    }
    if (-not $nested) { $orphans.Add($componentId) }
  }
  if ($orphans.Count -gt 0) {
    throw "MSI components not reachable from any feature (their files would never be installed):`n$($orphans -join "`n")"
  }
  Write-Host ("    component mapping verified: {0} components, none orphaned" -f $componentNodes.Count) -ForegroundColor Green

  & $brandingCheck -InstallerDirectory $installerDir -WixSource $wxs -InnoSource (Join-Path $installerDir 'BuildAI.iss') -DecompiledWxs $decompiledWxs
  Remove-Item $verifyRoot -Recurse -Force
}

# --- Verify binary references inside the staged payload -----------------------
# Every .addin manifest names the assembly Revit must load. If that assembly is
# missing, or an assembly ships without a manifest pointing at it, Revit either
# reports a load failure or silently ignores a plugin that is physically present.
Write-Host "==> Verifying add-in manifest and assembly references" -ForegroundColor Cyan
$referenceProblems = New-Object System.Collections.Generic.List[string]
foreach ($version in $Versions) {
  $versionRoot = Join-Path $payloadRoot $version
  if (-not (Test-Path -LiteralPath $versionRoot)) {
    $referenceProblems.Add("Revit ${version}: payload folder is missing.")
    continue
  }

  $manifests = @(Get-ChildItem -LiteralPath $versionRoot -Filter '*.addin' -File)
  if ($manifests.Count -eq 0) {
    $referenceProblems.Add("Revit ${version}: no .addin manifests staged. Revit would load nothing.")
    continue
  }

  $declaredAssemblies = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
  foreach ($manifest in $manifests) {
    [xml]$manifestXml = Get-Content -LiteralPath $manifest.FullName -Raw
    $addInNodes = @($manifestXml.SelectNodes("//*[local-name()='AddIn']"))
    if ($addInNodes.Count -eq 0) {
      $referenceProblems.Add("Revit ${version}: $($manifest.Name) declares no AddIn element.")
      continue
    }
    foreach ($addIn in $addInNodes) {
      $assembly = ($addIn.SelectSingleNode("*[local-name()='Assembly']")).InnerText
      $fullClass = ($addIn.SelectSingleNode("*[local-name()='FullClassName']")).InnerText
      $addInId = ($addIn.SelectSingleNode("*[local-name()='AddInId']")).InnerText

      if ([string]::IsNullOrWhiteSpace($assembly)) {
        $referenceProblems.Add("Revit ${version}: $($manifest.Name) has an empty Assembly element.")
        continue
      }
      if ([System.IO.Path]::IsPathRooted($assembly) -or $assembly.Contains('\') -or $assembly.Contains('/')) {
        $referenceProblems.Add("Revit ${version}: $($manifest.Name) uses a path '$assembly' instead of a bare file name next to the manifest.")
      }
      if ([string]::IsNullOrWhiteSpace($fullClass)) {
        $referenceProblems.Add("Revit ${version}: $($manifest.Name) has an empty FullClassName; Revit cannot instantiate the entry point.")
      }
      $parsedGuid = [Guid]::Empty
      if (-not [Guid]::TryParse($addInId, [ref]$parsedGuid)) {
        $referenceProblems.Add("Revit ${version}: $($manifest.Name) has an invalid AddInId '$addInId'.")
      }

      $assemblyPath = Join-Path $versionRoot $assembly
      if (-not (Test-Path -LiteralPath $assemblyPath -PathType Leaf)) {
        $referenceProblems.Add("Revit ${version}: $($manifest.Name) references '$assembly', which is not staged next to it.")
      }
      else {
        [void]$declaredAssemblies.Add($assembly)
      }
    }
  }

  # Plugin assemblies that no manifest points at would be installed but never loaded.
  foreach ($pluginDll in @(Get-ChildItem -LiteralPath $versionRoot -Filter 'BuildAI.Plugin*.dll' -File)) {
    if (-not $declaredAssemblies.Contains($pluginDll.Name)) {
      $referenceProblems.Add("Revit ${version}: $($pluginDll.Name) is staged but no .addin manifest references it; Revit will never load it.")
    }
  }
}
if ($referenceProblems.Count -gt 0) {
  throw "Add-in manifest / assembly reference verification failed:`n$($referenceProblems -join "`n")"
}
Write-Host "    add-in manifest and assembly references verified" -ForegroundColor Green

# --- Build a standalone cleanup EXE for testers -------------------------------
# The tester does not need PowerShell. IExpress is a standard Windows component
# and is used only on the build machine to package the pure-CMD cleanup logic.
$uninstallerBuilder = Join-Path $root 'tools\Build-StandaloneUninstaller.ps1'
$uninstallerExe = Join-Path $outputDir 'BuildAI_Full_Cleanup.exe'
if (-not (Test-Path $uninstallerBuilder -PathType Leaf)) {
  throw "Standalone uninstaller builder was not found: $uninstallerBuilder"
}
& $currentPowerShellExe -NoLogo -NoProfile -ExecutionPolicy Bypass -File $uninstallerBuilder -OutputPath $uninstallerExe
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $uninstallerExe -PathType Leaf)) {
  throw "Standalone BuildAI cleanup EXE was not created."
}

# Keep a pure-CMD fallback next to the EXE. It also requires no PowerShell.
$cleanupCmd = Join-Path $root 'tools\Uninstall-All-BuildAI.cmd'
if (Test-Path $cleanupCmd) {
  Copy-Item $cleanupCmd (Join-Path $outputDir 'Uninstall-All-BuildAI.cmd') -Force
}

# Sign the complete output set only after the cleanup EXE has been created.
# Signing earlier would leave that final BuildAI-owned executable unsigned.
if ($SigningPfxPath) {
  Write-Host "==> Signing MSI and final BuildAI executable outputs" -ForegroundColor Cyan
  & $currentPowerShellExe -NoLogo -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tools\Sign-BuildArtifacts.ps1') `
      -Path $outputDir -PfxPath $SigningPfxPath -PfxPassword $SigningPfxPassword
  if ($LASTEXITCODE -ne 0) { throw "Final output signing failed." }
}

# --- Build the user-facing attached WiX Burn EXE ----------------------------
$setupExe = Join-Path $outputDir 'BuildAI_RevitPlugins_7.4_Setup.exe'
& (Join-Path $root 'tools/Build-SingleFileBundle.ps1') -InstallerDirectory $installerDir -MsiPath $msi -OutputPath $setupExe -Version '7.4' -SigningPfxPath $SigningPfxPath -SigningPfxPassword $SigningPfxPassword

# Final application-control packaging audit. This verifies the embedded-CAB
# MSI design, absence of executable custom actions, clean ADS state and the
# Authenticode status of BuildAI-owned binaries. Unsigned files are reported
# but are fatal only when a signing certificate was requested for this build.
Clear-InternetZoneIdentifier -Path $outputDir
Assert-NoInternetZoneIdentifier -Path $outputDir
$requireValidSignatures = -not [string]::IsNullOrWhiteSpace($SigningPfxPath)
$applicationControlAudit = Join-Path $root 'tools\Test-ApplicationControlPackaging.ps1'
if ($requireValidSignatures) {
  & $currentPowerShellExe -NoLogo -NoProfile -ExecutionPolicy Bypass -File $applicationControlAudit `
      -ProjectRoot $root -RequireValidBuildAiSignature
} else {
  # Omit the switch when signature enforcement is disabled.
  & $currentPowerShellExe -NoLogo -NoProfile -ExecutionPolicy Bypass -File $applicationControlAudit `
      -ProjectRoot $root
}
if ($LASTEXITCODE -ne 0) { throw "Application-control packaging audit failed." }

$size = (Get-Item $msi).Length
$cleanupSize = (Get-Item $uninstallerExe).Length
Write-Host "" 
Write-Host "Build completed successfully." -ForegroundColor Green
Write-Host ("MSI: installer\output\BuildAI_RevitSuite_7.4_Setup.msi ({0:N0} bytes)" -f $size) -ForegroundColor Green
Write-Host ("Cleanup EXE: installer\output\BuildAI_Full_Cleanup.exe ({0:N0} bytes)" -f $cleanupSize) -ForegroundColor Green
Write-Host ("Setup EXE: installer\output\BuildAI_RevitPlugins_7.4_Setup.exe ({0:N0} bytes)" -f (Get-Item $setupExe).Length) -ForegroundColor Green
Write-Host "The MSI performs a safe registered upgrade, including replacement of an earlier package with the same product version. It contains no executable cleanup custom actions." -ForegroundColor DarkGray


