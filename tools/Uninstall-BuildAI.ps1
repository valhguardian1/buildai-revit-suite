<#
.SYNOPSIS
  Quickly removes all BuildAI Revit plugins from Revit 2023-2026. Self-elevates.
.USAGE
  pwsh ./tools/Uninstall-BuildAI.ps1 [-Versions 2023,2024,2025,2026]
#>
param([string[]]$Versions = @('2023','2024','2025','2026'))

$ErrorActionPreference = 'Stop'
$id = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $id.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
  Start-Process -FilePath (Get-Process -Id $PID).Path `
    -ArgumentList '-ExecutionPolicy','Bypass','-File',"`"$PSCommandPath`"",'-Versions',($Versions -join ',') `
    -Verb RunAs
  return
}

$running = Get-Process Revit -ErrorAction SilentlyContinue
if ($running) {
  Write-Warning 'Revit is running. Close Revit before uninstalling BuildAI.'
  Read-Host 'Press Enter after Revit is closed'
}

$addins = Join-Path $env:ProgramData 'Autodesk\Revit\Addins'
$removed = 0
foreach ($v in $Versions) {
  $base = Join-Path $addins $v
  $targets = @(
    (Join-Path $base 'BuildAI'),
    (Join-Path $base 'BuildAI.Plugin1.addin'),
    (Join-Path $base 'BuildAI.Plugin2.addin'),
    (Join-Path $base 'BuildAI.Plugin3.addin'),
    (Join-Path $base 'BuildAI.Plugin4.addin'),
    (Join-Path $base 'BuildAI.Plugin5.addin')
  )

  foreach ($p in $targets) {
    if (Test-Path $p) {
      Remove-Item $p -Recurse -Force
      Write-Host "Removed $p" -ForegroundColor Yellow
      $removed++
    }
  }
}

# Remove installer-owned files and registration when the regular Inno uninstaller exists.
$appDir = Join-Path $env:ProgramFiles 'BuildAI\RevitPlugins'
$innoUninstaller = Join-Path $appDir 'unins000.exe'
if (Test-Path $innoUninstaller) {
  Write-Host 'Running registered BuildAI uninstaller...' -ForegroundColor Cyan
  Start-Process -FilePath $innoUninstaller -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART' -Wait
}

Write-Host "Uninstall complete. Removed targets: $removed" -ForegroundColor Cyan
