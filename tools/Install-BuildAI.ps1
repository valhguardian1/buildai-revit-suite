<#
.SYNOPSIS
  Installs the BuildAI plugin WITHOUT an installer .exe, by copying the staged
  payload into the Revit Addins folder. Run build.ps1 first to create the payload.
  Self-elevates because %ProgramData% requires admin.

.USAGE
  pwsh ./tools/Install-BuildAI.ps1                     # all staged versions
  pwsh ./tools/Install-BuildAI.ps1 -Versions 2025,2026
#>
param([string[]]$Versions = @('2023','2024','2025','2026'))

$ErrorActionPreference = 'Stop'

# self-elevate
$id = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $id.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
  Start-Process -FilePath (Get-Process -Id $PID).Path -ArgumentList "-ExecutionPolicy","Bypass","-File","`"$PSCommandPath`"","-Versions",($Versions -join ',') -Verb RunAs
  return
}

$payloadRoot = Join-Path (Split-Path $PSScriptRoot -Parent) 'installer\payload'
$addins = Join-Path $env:ProgramData 'Autodesk\Revit\Addins'

foreach ($v in $Versions) {
  $src = Join-Path $payloadRoot $v
  if (-not (Test-Path $src)) { Write-Warning "No staged payload for $v (run build.ps1). Skipping."; continue }
  $dst = Join-Path $addins $v
  $sourceFiles = @(Get-ChildItem -LiteralPath $src -File -Recurse -Force)

  # Remove Mark-of-the-Web from the staged files before they are copied. This
  # fallback installer is commonly used from a downloaded/extracted archive.
  foreach ($file in $sourceFiles) {
    Unblock-File -LiteralPath $file.FullName -ErrorAction Stop
  }

  New-Item -ItemType Directory -Force -Path $dst | Out-Null
  Copy-Item (Join-Path $src '*') $dst -Recurse -Force

  # Clear and verify only the exact files copied by this run. Never unblock the
  # complete Revit Addins directory because it may contain unrelated add-ins.
  $stillMarked = New-Object System.Collections.Generic.List[string]
  foreach ($file in $sourceFiles) {
    $relativePath = $file.FullName.Substring($src.Length).TrimStart('\','/')
    $installedFile = Join-Path $dst $relativePath
    Unblock-File -LiteralPath $installedFile -ErrorAction Stop
    $zoneStream = Get-Item -LiteralPath $installedFile -Stream 'Zone.Identifier' -ErrorAction SilentlyContinue
    if ($null -ne $zoneStream) { $stillMarked.Add($installedFile) }
  }

  if ($stillMarked.Count -gt 0) {
    throw "Zone.Identifier remains on installed BuildAI files for Revit ${v}:`n$($stillMarked -join "`n")"
  }

  Write-Host "Installed BuildAI for Revit $v -> $dst" -ForegroundColor Green
}
Write-Host "Done. Start Revit and open the BuildAI ribbon tab." -ForegroundColor Cyan
