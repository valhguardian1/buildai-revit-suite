param(
  [Parameter(Mandatory=$true)][string]$InstallerDirectory,
  [Parameter(Mandatory=$true)][string]$WixSource,
  [string]$InnoSource = '',
  [string]$BundleSource = '',
  [string]$SetupExe = '',
  [string]$DecompiledWxs = ''
)
$ErrorActionPreference = 'Stop'

$productIcon = Join-Path $InstallerDirectory 'assets\BuildAI.ico'
if (-not (Test-Path -LiteralPath $productIcon -PathType Leaf)) { throw "Required product icon is missing: $productIcon" }
$bytes = [IO.File]::ReadAllBytes($productIcon)
if ($bytes.Length -lt 100 -or $bytes[0] -ne 0 -or $bytes[1] -ne 0 -or $bytes[2] -ne 1 -or $bytes[3] -ne 0) {
  throw "Required product icon is not a valid ICO file: $productIcon"
}
$wix = Get-Content -LiteralPath $WixSource -Raw
if ($wix -notmatch '<Icon\s+Id="BuildAIIcon"\s+SourceFile="assets\\BuildAI\.ico"' -or
    $wix -notmatch '<Property\s+Id="ARPPRODUCTICON"\s+Value="BuildAIIcon"') {
  throw "WiX installer is missing the BuildAI product icon or ARPPRODUCTICON: $WixSource"
}

if ($InnoSource) {
  $setupIcon = Join-Path $InstallerDirectory 'assets\BuildAI_Setup.ico'
  if (-not (Test-Path -LiteralPath $setupIcon -PathType Leaf)) { throw "Required setup EXE icon is missing: $setupIcon" }
  $setupBytes = [IO.File]::ReadAllBytes($setupIcon)
  if ($setupBytes.Length -lt 100 -or $setupBytes[0] -ne 0 -or $setupBytes[1] -ne 0 -or $setupBytes[2] -ne 1 -or $setupBytes[3] -ne 0) {
    throw "Required setup EXE icon is not a valid ICO file: $setupIcon"
  }
  $inno = Get-Content -LiteralPath $InnoSource -Raw
  if ($inno -notmatch '(?m)^SetupIconFile=assets\\BuildAI_Setup\.ico\s*$' -or
      $inno -notmatch '(?m)^UninstallDisplayIcon=\{app\}\\BuildAI\.ico\s*$') {
    throw "Inno Setup is missing the required BuildAI setup or uninstall icon: $InnoSource"
  }
}

if ($DecompiledWxs) {
  [xml]$msi = Get-Content -LiteralPath $DecompiledWxs -Raw
  $icon = $msi.SelectSingleNode("//*[local-name()='Icon' and @Id='BuildAIIcon']")
  $arp = $msi.SelectSingleNode("//*[local-name()='Property' and @Id='ARPPRODUCTICON' and @Value='BuildAIIcon']")
  if ($null -eq $icon -or $null -eq $arp) { throw 'Built MSI is missing BuildAIIcon or ARPPRODUCTICON.' }
}

if ($SetupExe) {
  if (-not (Test-Path -LiteralPath $SetupExe -PathType Leaf)) { throw "Setup EXE is missing: $SetupExe" }
  if (-not $InnoSource -and -not $BundleSource) { throw 'Installer source is required to check the EXE icon.' }
  & (Join-Path $PSScriptRoot 'Test-ExeIcon.ps1') -ExePath $SetupExe -IconPath (Join-Path $InstallerDirectory 'assets\BuildAI_Setup.ico')
}

Write-Host 'BuildAI installer icon checks passed.'
