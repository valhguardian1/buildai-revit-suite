param([Parameter(Mandatory=$true)][string]$ExePath,[Parameter(Mandatory=$true)][string]$ExpectedTitle,[switch]$LayoutOnly)
$ErrorActionPreference='Stop'
# Process-level smoke test: no UI Automation, input injection or Install action.
$dir=Join-Path ([IO.Path]::GetTempPath()) ('BuildAI-WindowTest-'+[guid]::NewGuid().ToString('N'))
$isolated=Join-Path $dir 'isolated'
New-Item -ItemType Directory -Path $isolated -Force | Out-Null
$exe=Join-Path $isolated (Split-Path $ExePath -Leaf)
Copy-Item -LiteralPath $ExePath -Destination $exe
if(@(Get-ChildItem -LiteralPath $isolated -Force).Count -ne 1){throw 'Smoke directory is not isolated.'}
$log=Join-Path $dir 'startup.log'
$arguments=@('/log',('"'+$log+'"'))
if($LayoutOnly){$arguments+=@('/layout',('"'+(Join-Path $dir 'layout')+'"'))}
$started=Start-Process -FilePath $exe -ArgumentList $arguments -PassThru
$owned=[System.Collections.Generic.HashSet[int]]::new()
[void]$owned.Add($started.Id)
$window=$null
try {
  $deadline=[DateTime]::UtcNow.AddSeconds(25)
  do {
    $processes=@(Get-CimInstance Win32_Process)
    foreach($item in $processes){if($owned.Contains([int]$item.ParentProcessId)){[void]$owned.Add([int]$item.ProcessId)}}
    foreach($id in @($owned)) {
      $candidate=Get-Process -Id $id -ErrorAction SilentlyContinue
      if($candidate -and $candidate.MainWindowHandle -ne 0 -and $candidate.MainWindowTitle -eq $ExpectedTitle){$window=$candidate;break}
    }
    if($window -and (Test-Path $log) -and (Get-Content $log -Raw) -match 'Detect complete, result: 0x0'){break}
    Start-Sleep -Milliseconds 250
  } while([DateTime]::UtcNow -lt $deadline)
  if(!$window){throw "No expected installer window was created: $ExpectedTitle"}
  Start-Sleep -Milliseconds 500
  $text=Get-Content -LiteralPath $log -Raw
  if($text -notmatch 'Detect complete, result: 0x0' -or $text -match '(?im)e000:|missing from the installation directory|Failed to') {throw "Bundle startup failed; inspect $log"}
  if($text -match 'Apply begin' -and (!$LayoutOnly -or $text -notmatch 'action: Layout' -or $text -match 'execute: (Install|Uninstall|Repair)')){throw 'Unexpected install action during GUI smoke test.'}
  Write-Host "Isolated EXE created window '$($window.MainWindowTitle)'; detection succeeded; no installation requested; layout=$LayoutOnly."
} finally {
  # Stop only the test process tree; no uninstall/install action is requested.
  foreach($id in @($owned | Sort-Object -Descending)){Stop-Process -Id $id -Force -ErrorAction SilentlyContinue}
}
