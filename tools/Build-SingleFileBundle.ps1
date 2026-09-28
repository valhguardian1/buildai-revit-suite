param(
  [Parameter(Mandatory=$true)][string]$InstallerDirectory,
  [Parameter(Mandatory=$true)][string]$MsiPath,
  [Parameter(Mandatory=$true)][string]$OutputPath,
  [Parameter(Mandatory=$true)][string]$Version,
  [string]$SigningPfxPath = '',
  [string]$SigningPfxPassword = ''
)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$wixVersion=(& wix --version | Out-String).Trim()
if($wixVersion -notmatch '^5\.0\.2(?:\+|$)'){throw 'This release requires WiX CLI 5.0.2.'}
$InstallerDirectory=(Resolve-Path $InstallerDirectory).Path
$MsiPath=(Resolve-Path $MsiPath).Path
$OutputPath=[IO.Path]::GetFullPath($OutputPath)
$parsedVersion=[version]$Version
$bundleVersion='{0}.{1}.{2}.{3}' -f $parsedVersion.Major,$parsedVersion.Minor,[Math]::Max(0,$parsedVersion.Build),[Math]::Max(0,$parsedVersion.Revision)
$source=Join-Path $InstallerDirectory 'BuildAI.Bundle.wxs'
foreach($asset in @('BuildAI_Setup.ico','BuildAI.png')) {
  if(!(Test-Path -LiteralPath (Join-Path $InstallerDirectory "assets/$asset"))) { throw "Required Bundle branding asset missing: $asset" }
}
$xml=[xml](Get-Content -LiteralPath $source -Raw)
if($xml.Wix.Bundle.Compressed -ne 'yes' -or $xml.Wix.Bundle.IconSourceFile -ne 'assets\BuildAI_Setup.ico') { throw 'Bundle must embed payload and the BuildAI setup icon.' }
$extensions=@()
foreach($id in @('WixToolset.Bal.wixext','WixToolset.Util.wixext')) {
  $dll=if($id -eq 'WixToolset.Bal.wixext'){'WixToolset.BootstrapperApplications.wixext.dll'}else{'WixToolset.Util.wixext.dll'}
  $extension=Join-Path $root ('.wix/extensions/'+$id+'/5.0.2/wixext5/'+$dll)
  if(!(Test-Path -LiteralPath $extension)) {
    Push-Location $root
    try { wix extension add ($id+'/5.0.2'); if($LASTEXITCODE -ne 0){throw "Cannot restore $id 5.0.2."} }
    finally { Pop-Location }
  }
  $extensions+=@('-ext',$extension)
}
# Fresh dedicated output prevents stale detached files from passing verification.
$stage=Join-Path (Split-Path $OutputPath -Parent) ('bundle-stage-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage -Force | Out-Null
$stagedExe=Join-Path $stage (Split-Path $OutputPath -Leaf)
Push-Location $InstallerDirectory
try {
  wix build $source @extensions -d "MsiPath=$MsiPath" -d "BundleVersion=$bundleVersion" -o $stagedExe
  if($LASTEXITCODE -ne 0){throw 'WiX Burn Bundle build failed.'}
} finally { Pop-Location }
if($SigningPfxPath) {
  $engine=Join-Path $stage 'engine.exe'
  wix burn detach $stagedExe -o $engine
  if($LASTEXITCODE -ne 0){throw 'Burn engine detach for signing failed.'}
  & (Join-Path $PSScriptRoot 'Sign-BuildArtifacts.ps1') -Path $engine -PfxPath $SigningPfxPath -PfxPassword $SigningPfxPassword
  $signed=Join-Path $stage 'signed.exe'
  wix burn reattach $stagedExe -engine $engine -o $signed
  if($LASTEXITCODE -ne 0){throw 'Burn engine reattach after signing failed.'}
  & (Join-Path $PSScriptRoot 'Sign-BuildArtifacts.ps1') -Path $signed -PfxPath $SigningPfxPath -PfxPassword $SigningPfxPassword
  Copy-Item -LiteralPath $signed -Destination $stagedExe -Force
}
& (Join-Path $PSScriptRoot 'Test-SingleFileBundle.ps1') -ExePath $stagedExe -MsiPath $MsiPath -Version $Version -InstallerDirectory $InstallerDirectory
& (Join-Path $PSScriptRoot 'Test-BundleWindow.ps1') -ExePath $stagedExe -ExpectedTitle ($xml.Wix.Bundle.Name+' Setup') -LayoutOnly
Copy-Item -LiteralPath $stagedExe -Destination $OutputPath -Force
Write-Host "Verified single-file Bundle: $OutputPath"
