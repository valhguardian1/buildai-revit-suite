[CmdletBinding()]
param(
  [ValidateSet('Verify', 'Release')]
  [string]$Mode = 'Verify',
  [string]$SigningPfxPath = '',
  [string]$SigningPfxPassword = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$config = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'harness.json') -Raw | ConvertFrom-Json
$baselinePath = Join-Path $PSScriptRoot 'protected-baseline.json'
$summaryPath = Join-Path $root $config.summaryPath
$runId = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')
$workDir = Join-Path $root (Join-Path $config.workRoot $runId)
$logPath = Join-Path $workDir 'build.log'
$started = [DateTime]::UtcNow
$stage = 'initialization'
$oldVersion = $null
$newVersion = $null
$versionBackups = @{}
$powerShellCandidate = Join-Path $env:ProgramFiles 'PowerShell\7\pwsh.exe'
if (Test-Path -LiteralPath $powerShellCandidate -PathType Leaf) {
  $powerShellExe = $powerShellCandidate
}
else {
  $pwshCommand = Get-Command pwsh.exe -ErrorAction SilentlyContinue | Select-Object -First 1
  $powerShellExe = if ($pwshCommand) { $pwshCommand.Source } else { (Get-Process -Id $PID -ErrorAction Stop).Path }
}

New-Item -ItemType Directory -Path $workDir -Force | Out-Null
New-Item -ItemType Directory -Path (Split-Path $summaryPath -Parent) -Force | Out-Null

function Write-Summary([string]$Status, [string]$Message, [string]$Artifact = '') {
  $summary = [ordered]@{
    schemaVersion = 1
    runId = $runId
    mode = $Mode
    status = $Status
    stage = $stage
    startedAtUtc = $started.ToString('o')
    finishedAtUtc = [DateTime]::UtcNow.ToString('o')
    durationSeconds = [math]::Round(([DateTime]::UtcNow - $started).TotalSeconds, 2)
    versionBefore = $oldVersion
    versionAfter = $newVersion
    artifact = $Artifact
    failedLog = $(if ($Status -eq 'failed') { $logPath.Substring($root.Length + 1).Replace('\','/') } else { $null })
    message = $Message
  }
  $summary | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $summaryPath -Encoding utf8
}

function Assert-ProtectedBaseline {
  if (-not (Test-Path -LiteralPath $baselinePath -PathType Leaf)) {
    throw 'Protected baseline is missing. Run harness/Set-ProtectedBaseline.ps1 with an explicit reason.'
  }
  $baseline = Get-Content -LiteralPath $baselinePath -Raw | ConvertFrom-Json
  $problems = [System.Collections.Generic.List[string]]::new()
  foreach ($property in $baseline.files.PSObject.Properties) {
    $relativePath = $property.Name
    $path = Join-Path $root $relativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
      $problems.Add("missing: $relativePath")
      continue
    }
    $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne [string]$property.Value) { $problems.Add("changed: $relativePath") }
  }
  if ($problems.Count -gt 0) {
    throw "Protected behavior baseline changed without approval:`n$($problems -join "`n")"
  }
}

function Invoke-FullBuild([string]$Label) {
  $script = Join-Path $root $config.buildScript
  $arguments = @('-NoLogo', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $script)
  if ($SigningPfxPath) { $arguments += @('-SigningPfxPath', $SigningPfxPath) }
  if ($SigningPfxPassword) { $arguments += @('-SigningPfxPassword', $SigningPfxPassword) }
  "===== $Label =====" | Add-Content -LiteralPath $logPath -Encoding utf8
  & $powerShellExe @arguments 2>&1 | Tee-Object -FilePath $logPath -Append
  if ($LASTEXITCODE -ne 0) { throw "$Label failed with exit code $LASTEXITCODE." }
}

function Get-ProductVersion {
  [xml]$props = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw
  return [version]$props.Project.PropertyGroup.Version[0]
}

function Set-ProductVersion([version]$From, [version]$To) {
  foreach ($relativePath in $config.versionFiles) {
    $path = Join-Path $root $relativePath
    $text = [IO.File]::ReadAllText($path)
    $versionBackups[$path] = $text
    $fromFull = '{0}.{1}.{2}.0' -f $From.Major,$From.Minor,[Math]::Max(0,$From.Build)
    $toFull = '{0}.{1}.{2}.0' -f $To.Major,$To.Minor,[Math]::Max(0,$To.Build)
    $updated = $text.Replace($fromFull,$toFull)
    $updated = [regex]::Replace($updated,([regex]::Escape($From.ToString())+'(?![0-9]|\.[0-9])'),$To.ToString())
    if ($updated -eq $text) { throw "Version token $From was not found in $relativePath" }
    [IO.File]::WriteAllText($path, $updated, [Text.UTF8Encoding]::new($false))
  }
}

function Restore-VersionFiles {
  foreach ($entry in $versionBackups.GetEnumerator()) {
    [IO.File]::WriteAllText($entry.Key, $entry.Value, [Text.UTF8Encoding]::new($false))
  }
}

function Remove-WorkspaceDirectory([string]$Path) {
  $fullRoot = [IO.Path]::GetFullPath($root).TrimEnd('\') + '\'
  $fullPath = [IO.Path]::GetFullPath($Path)
  if (-not $fullPath.StartsWith($fullRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing cleanup outside project root: $fullPath"
  }
  if (Test-Path -LiteralPath $fullPath) { Remove-Item -LiteralPath $fullPath -Recurse -Force }
}

try {
  $stage = 'protected-baseline'
  Assert-ProtectedBaseline
  $productVersion = Get-ProductVersion
  $oldVersion = if ($productVersion.Build -lt 0) { $productVersion.ToString(2) } else { $productVersion.ToString(3) }

  $stage = 'preflight-full-build'
  Invoke-FullBuild 'preflight full build and all tests'

  if ($Mode -eq 'Verify') {
    $stage = 'complete'
    Write-Summary 'passed' 'Protected baseline, full build, all tests, and MSI verification passed.'
    Remove-WorkspaceDirectory $workDir
    Write-Host "Harness verification passed. Summary: $summaryPath" -ForegroundColor Green
    exit 0
  }

  $stage = 'version-bump'
  $from = [version]$oldVersion
  $to = [version]::new($from.Major, $from.Minor, [Math]::Max(0,$from.Build) + 1)
  $newVersion = $to.ToString(3)
  Set-ProductVersion $from $to

  $stage = 'final-full-build'
  Invoke-FullBuild "final full build and all tests for $newVersion"

  $stage = 'publish-exe-only'
  $builtExe = Join-Path $root "installer\output\BuildAI_RevitPlugins_${newVersion}_Setup.exe"
  if (-not (Test-Path -LiteralPath $builtExe -PathType Leaf)) { throw "Expected EXE was not produced: $builtExe" }
  $releaseRoot = Join-Path (Join-Path $root $config.releaseRoot) $newVersion
  if(Test-Path $releaseRoot){throw 'Release destination already exists; existing releases are preserved.'}
  New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
  $releaseExe = Join-Path $releaseRoot (Split-Path $builtExe -Leaf)
  Copy-Item -LiteralPath $builtExe -Destination $releaseExe -Force
  ((Get-FileHash $releaseExe).Hash.ToLowerInvariant()+'  '+(Split-Path $releaseExe -Leaf)) | Set-Content (Join-Path $releaseRoot 'SHA256SUMS.txt') -Encoding ascii

  $artifactRelative = $releaseExe.Substring($root.Length + 1).Replace('\','/')
  $stage = 'complete'
  Write-Summary 'passed' 'Preflight passed, patch version was increased, final full build passed, and only the autonomous EXE was published.' $artifactRelative
  Remove-WorkspaceDirectory $workDir
  Write-Host "Release harness passed: $releaseExe" -ForegroundColor Green
}
catch {
  if ($versionBackups.Count -gt 0) {
    Restore-VersionFiles
    $newVersion = $null
  }
  Write-Summary 'failed' $_.Exception.Message
  Write-Error "Harness failed at '$stage'. Read $summaryPath first; detailed log: $logPath"
  exit 1
}
