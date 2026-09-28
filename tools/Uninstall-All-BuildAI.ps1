<#
.SYNOPSIS
  Completely removes all known BuildAI Revit plugin test builds.
.DESCRIPTION
  - silently uninstalls MSI packages registered as BuildAI Revit products;
  - runs legacy Inno uninstallers when present;
  - removes leftover Revit add-in manifests and BuildAI payload files;
  - optionally removes BuildAI user data, logs, snapshots and cached reports;
  - self-elevates when Administrator rights are required.
#>
[CmdletBinding()]
param(
  [string[]]$Versions = @('2023','2024','2025','2026'),
  [switch]$KeepUserData,
  [switch]$ForceCloseRevit,
  [switch]$NoPause
)

$ErrorActionPreference = 'Continue'
Set-StrictMode -Version 2.0

function Write-Step([string]$Text) { Write-Host "`n==> $Text" -ForegroundColor Cyan }
function Write-Ok([string]$Text)   { Write-Host "[OK] $Text" -ForegroundColor Green }
function Write-Warn([string]$Text) { Write-Host "[WARN] $Text" -ForegroundColor Yellow }

function Test-Administrator {
  $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
  $principal = New-Object Security.Principal.WindowsPrincipal($identity)
  return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Administrator)) {
  $arguments = @(
    '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"{0}"' -f $PSCommandPath),
    '-Versions', ($Versions -join ',')
  )
  if ($KeepUserData) { $arguments += '-KeepUserData' }
  if ($ForceCloseRevit) { $arguments += '-ForceCloseRevit' }
  if ($NoPause) { $arguments += '-NoPause' }
  Start-Process -FilePath 'powershell.exe' -ArgumentList $arguments -Verb RunAs
  exit
}

Write-Host 'BuildAI Revit Suite - complete test cleanup' -ForegroundColor White

$revit = Get-Process -Name Revit -ErrorAction SilentlyContinue
if ($revit) {
  if ($ForceCloseRevit) {
    Write-Step 'Closing Revit'
    $revit | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    Write-Ok 'Revit processes closed.'
  } else {
    Write-Warn 'Revit is running. Close it before cleanup.'
    if (-not $NoPause) { Read-Host 'Press Enter after Revit is closed' | Out-Null }
    if (Get-Process -Name Revit -ErrorAction SilentlyContinue) {
      Write-Warn 'Revit is still running. Locked files may remain.'
    }
  }
}

Write-Step 'Uninstalling registered BuildAI MSI packages'
$registryRoots = @(
  'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
  'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
  'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*'
)
$products = foreach ($root in $registryRoots) {
  Get-ItemProperty $root -ErrorAction SilentlyContinue | Where-Object {
    $_.DisplayName -and (
      $_.DisplayName -like 'BuildAI Revit*' -or
      $_.Publisher -eq 'BuildAI' -or
      $_.DisplayName -like '*BuildAI*Revit*'
    )
  }
}
$products = $products | Sort-Object PSChildName -Unique

if (-not $products) {
  Write-Warn 'No registered BuildAI MSI packages found.'
} else {
  foreach ($product in $products) {
    $productCode = $null
    if ($product.PSChildName -match '^\{[0-9A-Fa-f-]{36}\}$') {
      $productCode = $product.PSChildName
    } elseif ($product.UninstallString -match '\{[0-9A-Fa-f-]{36}\}') {
      $productCode = $Matches[0]
    }

    if ($productCode) {
      Write-Host "Removing MSI: $($product.DisplayName) $productCode"
      $process = Start-Process -FilePath 'msiexec.exe' -ArgumentList @('/x', $productCode, '/qn', '/norestart') -Wait -PassThru
      if ($process.ExitCode -in @(0,1605,1614,1641,3010)) {
        Write-Ok "Removed $($product.DisplayName)"
      } else {
        Write-Warn "MSI returned exit code $($process.ExitCode) for $($product.DisplayName)"
      }
    } else {
      Write-Warn "Could not determine ProductCode for $($product.DisplayName)"
    }
  }
}

Write-Step 'Running legacy Inno uninstallers'
$legacyRoots = @(
  (Join-Path $env:ProgramFiles 'BuildAI'),
  $(if (${env:ProgramFiles(x86)}) { Join-Path ${env:ProgramFiles(x86)} 'BuildAI' }),
  (Join-Path $env:LOCALAPPDATA 'Programs\BuildAI')
) | Where-Object { $_ }

$legacyUninstallers = foreach ($root in $legacyRoots) {
  if (Test-Path $root) {
    Get-ChildItem $root -Filter 'unins*.exe' -Recurse -File -ErrorAction SilentlyContinue
  }
}
foreach ($uninstaller in ($legacyUninstallers | Sort-Object FullName -Unique)) {
  Write-Host "Running: $($uninstaller.FullName)"
  try {
    Start-Process -FilePath $uninstaller.FullName -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART' -Wait
    Write-Ok "Legacy uninstaller completed: $($uninstaller.Name)"
  } catch {
    Write-Warn "Legacy uninstaller failed: $($_.Exception.Message)"
  }
}

Write-Step 'Removing Revit add-in leftovers'
$addinRoot = Join-Path $env:ProgramData 'Autodesk\Revit\Addins'
$removed = 0
foreach ($version in $Versions) {
  $versionRoot = Join-Path $addinRoot $version
  if (-not (Test-Path $versionRoot)) { continue }

  $exactTargets = @(
    (Join-Path $versionRoot 'BuildAI'),
    (Join-Path $versionRoot 'BuildAI.Plugin1.addin'),
    (Join-Path $versionRoot 'BuildAI.Plugin2.addin'),
    (Join-Path $versionRoot 'BuildAI.Plugin3.addin'),
    (Join-Path $versionRoot 'BuildAI.Plugin4.addin'),
    (Join-Path $versionRoot 'BuildAI.Plugin5.addin')
  )
  foreach ($target in $exactTargets) {
    if (Test-Path $target) {
      Remove-Item $target -Recurse -Force -ErrorAction SilentlyContinue
      $removed++
      Write-Host "Removed $target"
    }
  }

  $patterns = @('BuildAI*.addin','BuildAI*.dll','BuildAI*.pdb','BuildAI*.deps.json','BuildAI*.runtimeconfig.json')
  foreach ($pattern in $patterns) {
    Get-ChildItem $versionRoot -Filter $pattern -File -ErrorAction SilentlyContinue | ForEach-Object {
      Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue
      $removed++
      Write-Host "Removed $($_.FullName)"
    }
  }
}
Write-Ok "Removed $removed Revit add-in leftovers."

Write-Step 'Removing legacy application folders'
$folders = @(
  (Join-Path $env:ProgramFiles 'BuildAI\RevitPlugins'),
  (Join-Path $env:ProgramFiles 'BuildAI\Revit Suite'),
  $(if (${env:ProgramFiles(x86)}) { Join-Path ${env:ProgramFiles(x86)} 'BuildAI\RevitPlugins' }),
  (Join-Path $env:LOCALAPPDATA 'Programs\BuildAI\RevitPlugins')
) | Where-Object { $_ }
foreach ($folder in $folders) {
  if (Test-Path $folder) {
    Remove-Item $folder -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "Removed $folder"
  }
}

if (-not $KeepUserData) {
  Write-Step 'Removing BuildAI test data and caches'
  $userData = Join-Path $env:LOCALAPPDATA 'BuildAI'
  if (Test-Path $userData) {
    Remove-Item $userData -Recurse -Force -ErrorAction SilentlyContinue
    Write-Ok "Removed $userData"
  } else {
    Write-Warn 'No BuildAI user-data directory found.'
  }
} else {
  Write-Warn 'User data was preserved because -KeepUserData was specified.'
}

Write-Step 'Cleanup completed'
Write-Host 'All detected BuildAI Revit test builds and leftovers were removed.' -ForegroundColor Green
Write-Host 'A Windows restart is normally not required.' -ForegroundColor DarkGray
if (-not $NoPause) { Read-Host 'Press Enter to close' | Out-Null }
