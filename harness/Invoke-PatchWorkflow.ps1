[CmdletBinding()]
param(
  [Parameter(Mandatory)]
  [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
  [string]$PatchPath,
  [ValidateSet('Assess', 'Apply')]
  [string]$Mode = 'Assess',
  [switch]$ApproveProtectedBehaviorChange,
  [string]$Reason = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$harnessConfig = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'harness.json') -Raw | ConvertFrom-Json
$policy = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'patch-policy.json') -Raw | ConvertFrom-Json
$baseline = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'protected-baseline.json') -Raw | ConvertFrom-Json
$patchPath = (Resolve-Path -LiteralPath $PatchPath).Path
$patchHash = (Get-FileHash -LiteralPath $patchPath -Algorithm SHA256).Hash.ToLowerInvariant()
$patchText = [IO.File]::ReadAllText($patchPath)
$summaryPath = Join-Path $root '.harness\patch-summary.json'
$runId = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')
$workDir = Join-Path $root ".harness\patch-work\$runId"
$logPath = Join-Path $workDir 'patch.log'
$status = 'assessing'
$message = ''
$backups = @{}

New-Item -ItemType Directory -Path (Split-Path $summaryPath -Parent) -Force | Out-Null

function Write-PatchSummary {
  $result = [ordered]@{
    schemaVersion = 1
    runId = $runId
    patch = Split-Path $patchPath -Leaf
    sha256 = $patchHash
    mode = $Mode
    status = $status
    applyability = $applyability
    risk = $risk
    files = @($targets)
    protectedFiles = @($protectedTargets)
    hunks = $hunkCount
    addedLines = $addedLines
    removedLines = $removedLines
    targetedTests = @($targetedTests)
    fullVerifyRequired = $true
    detailedLog = $(if (Test-Path -LiteralPath $logPath) { $logPath.Substring($root.Length + 1).Replace('\','/') } else { $null })
    message = $message
  }
  $result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $summaryPath -Encoding utf8
}

function Assert-CurrentBaseline {
  $problems = [System.Collections.Generic.List[string]]::new()
  foreach ($property in $baseline.files.PSObject.Properties) {
    $path = Join-Path $root $property.Name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { $problems.Add("missing: $($property.Name)"); continue }
    $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne [string]$property.Value) { $problems.Add("changed: $($property.Name)") }
  }
  if ($problems.Count) { throw "Repository does not match the protected baseline: $($problems -join '; ')" }
}

function Invoke-Test([string]$RelativePath) {
  $testPath = Join-Path $root $RelativePath
  "===== $RelativePath =====" | Add-Content -LiteralPath $logPath -Encoding utf8
  & $powerShellExe -NoLogo -NoProfile -ExecutionPolicy Bypass -File $testPath 2>&1 |
    Tee-Object -FilePath $logPath -Append
  if ($LASTEXITCODE -ne 0) { throw "Targeted test failed: $RelativePath" }
}

function Restore-Targets {
  foreach ($entry in $backups.GetEnumerator()) {
    if ($null -eq $entry.Value) { Remove-Item -LiteralPath $entry.Key -Force -ErrorAction SilentlyContinue }
    else { [IO.File]::WriteAllBytes($entry.Key, $entry.Value) }
  }
}

$headers = [regex]::Matches($patchText, '(?m)^diff --git a/(.+?) b/(.+?)\r?$')
$targets = @($headers | ForEach-Object { $_.Groups[2].Value.Replace('/', '\') } | Select-Object -Unique)
$hunkCount = ([regex]::Matches($patchText, '(?m)^@@ ')).Count
$addedLines = ([regex]::Matches($patchText, '(?m)^\+(?!\+\+\+)')).Count
$removedLines = ([regex]::Matches($patchText, '(?m)^-(?!---)')).Count
$protectedSet = @{}; foreach ($file in $baseline.files.PSObject.Properties.Name) { $protectedSet[$file.Replace('/', '\')] = $true }
$protectedTargets = @($targets | Where-Object { $protectedSet.ContainsKey($_) })
$targetedTests = @($policy.testRoutes | Where-Object {
  $route = $_; $targets | Where-Object { $_ -match $route.pathRegex }
} | ForEach-Object { $_.tests } | Select-Object -Unique)
$risk = if ($protectedTargets.Count) { 'high-protected-behavior' } elseif ($targets | Where-Object { $_ -match '(?i)(build|installer|\.csproj$|\.wxs$)' }) { 'medium-build-or-packaging' } else { 'normal' }
$applyability = 'unknown'
$powerShellCandidate = Join-Path $env:ProgramFiles 'PowerShell\7\pwsh.exe'
if (Test-Path -LiteralPath $powerShellCandidate) {
  $powerShellExe = $powerShellCandidate
}
else {
  $pwshCommand = Get-Command pwsh.exe -ErrorAction SilentlyContinue | Select-Object -First 1
  $powerShellExe = if ($pwshCommand) { $pwshCommand.Source } else { (Get-Process -Id $PID).Path }
}

try {
  if ($headers.Count -eq 0) { throw 'Only unified git-format patches are supported.' }
  if ($patchText -match '(?m)^GIT binary patch$|^Binary files ') { throw 'Binary patches are not allowed.' }
  if ($targets.Count -gt $policy.maxFiles) { throw "Patch changes $($targets.Count) files; limit is $($policy.maxFiles)." }
  if ($addedLines -gt $policy.maxAddedLines) { throw "Patch adds $addedLines lines; limit is $($policy.maxAddedLines)." }
  foreach ($target in $targets) {
    if ([IO.Path]::IsPathRooted($target) -or $target -match '(^|\\)\.\.(\\|$)') { throw "Unsafe patch path: $target" }
    if ([IO.Path]::GetExtension($target) -notin $policy.allowedExtensions) { throw "File type is not allowed by patch policy: $target" }
  }

  $checkOutput = @(& git -C $root apply --check --whitespace=error-all -- $patchPath 2>&1)
  if ($LASTEXITCODE -eq 0) { $applyability = 'clean' }
  else {
    $reverseOutput = @(& git -C $root apply --reverse --check --whitespace=error-all -- $patchPath 2>&1)
    $applyability = if ($LASTEXITCODE -eq 0) { 'already-applied' } else { 'conflict-or-superseded' }
    $message = (($checkOutput + $reverseOutput) | Select-Object -Unique | Select-Object -First 6) -join ' | '
  }

  if ($Mode -eq 'Assess') {
    $status = if ($applyability -eq 'clean') { 'ready' } elseif ($applyability -eq 'already-applied') { 'no-op' } else { 'rejected' }
    if (-not $message) { $message = "Patch assessment completed: $applyability." }
    Write-PatchSummary
    Write-Host "Patch assessment: $status ($applyability). Summary: $summaryPath" -ForegroundColor $(if ($status -eq 'ready') {'Green'} else {'Yellow'})
    exit $(if ($status -eq 'rejected') { 2 } else { 0 })
  }

  if ($applyability -ne 'clean') { throw "Patch cannot be applied safely: $applyability." }
  if ($protectedTargets.Count -and -not $ApproveProtectedBehaviorChange) {
    throw 'Patch changes protected behavior. Re-run with -ApproveProtectedBehaviorChange and -Reason after explicit approval.'
  }
  if ($protectedTargets.Count -and [string]::IsNullOrWhiteSpace($Reason)) { throw 'A reason is required for a protected behavior change.' }
  Assert-CurrentBaseline

  New-Item -ItemType Directory -Path $workDir -Force | Out-Null
  foreach ($target in $targets) {
    $path = Join-Path $root $target
    $backups[$path] = if (Test-Path -LiteralPath $path -PathType Leaf) { [IO.File]::ReadAllBytes($path) } else { $null }
  }
  & git -C $root apply --whitespace=error-all -- $patchPath 2>&1 | Tee-Object -FilePath $logPath -Append
  if ($LASTEXITCODE -ne 0) { throw 'git apply failed after a successful dry-run.' }

  foreach ($test in $targetedTests) { Invoke-Test $test }
  "===== full verify =====" | Add-Content -LiteralPath $logPath -Encoding utf8
  & $powerShellExe -NoLogo -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'build.ps1') 2>&1 |
    Tee-Object -FilePath $logPath -Append
  if ($LASTEXITCODE -ne 0) { throw 'Full build and test suite failed.' }

  if ($protectedTargets.Count) {
    & $powerShellExe -NoLogo -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Set-ProtectedBaseline.ps1') -Reason $Reason
    if ($LASTEXITCODE -ne 0) { throw 'Patch passed, but protected baseline update failed.' }
  }
  $status = 'applied-and-verified'
  $message = 'Patch applied; targeted tests and full build passed.'
  Remove-Item -LiteralPath $workDir -Recurse -Force
  Write-PatchSummary
}
catch {
  if ($backups.Count) { Restore-Targets }
  $status = 'rejected'
  if (-not $message) { $message = $_.Exception.Message }
  Write-PatchSummary
  Write-Error "Patch workflow rejected the patch. Read $summaryPath first. $($_.Exception.Message)"
  exit 1
}
