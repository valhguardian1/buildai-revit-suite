param([ValidateSet('Debug','Release')][string]$Configuration='Release')
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$installerDir=Join-Path $root 'installer'
$brandingCheck=Join-Path (Split-Path $root -Parent) 'tools\Test-InstallerBranding.ps1'
& $brandingCheck -InstallerDirectory $installerDir -WixSource (Join-Path $installerDir 'BuildAI.AccIssueReturn.wxs')
$props=Get-Content -Raw (Join-Path $root 'Directory.Build.props')
$version=[regex]::Match($props,'<Version>([^<]+)</Version>').Groups[1].Value
if([string]::IsNullOrWhiteSpace($version)){throw 'Directory.Build.props has no Version.'}
$payload=Join-Path $root 'installer\payload'
$out=Join-Path $root 'installer\output'
if(Test-Path $payload){Remove-Item -LiteralPath $payload -Recurse -Force}
New-Item $payload -ItemType Directory -Force|Out-Null
New-Item $out -ItemType Directory -Force|Out-Null

dotnet run --project (Join-Path $root 'tests\BuildAI.AccIssueReturn.Tests\BuildAI.AccIssueReturn.Tests.csproj') -c Release
if($LASTEXITCODE-ne 0){throw 'ACC Issue Return unit tests failed.'}
foreach($v in 2023,2024,2025,2026)
{
  $cfg="$Configuration R$($v-2000)"
  dotnet build (Join-Path $root 'src\BuildAI.AccIssueReturn.Revit\BuildAI.AccIssueReturn.Revit.csproj') -c $cfg --no-incremental
  if($LASTEXITCODE-ne 0){throw "Build failed for Revit $v."}
  $tfm=if($v-lt 2025){'net48'}else{'net8.0-windows'}
  $bin=Join-Path $root "src\BuildAI.AccIssueReturn.Revit\bin\$cfg\$tfm"
  $dest=Join-Path $payload $v
  New-Item $dest -ItemType Directory -Force|Out-Null
  $runtimeDir=Join-Path $dest 'BuildAI.AccIssueReturn'
  New-Item $runtimeDir -ItemType Directory -Force|Out-Null
  Get-ChildItem -LiteralPath $bin -File | Where-Object {$_.Extension-in @('.dll','.json')} | Copy-Item -Destination $runtimeDir -Force
  $manifestPath=Join-Path $dest 'BuildAI.AccIssueReturn.addin'
  $manifest=Get-Content -Raw (Join-Path $bin 'BuildAI.AccIssueReturn.addin')
  $manifest=$manifest.Replace('<Assembly>BuildAI.AccIssueReturn.Revit.dll</Assembly>','<Assembly>BuildAI.AccIssueReturn\BuildAI.AccIssueReturn.Revit.dll</Assembly>')
  Set-Content -LiteralPath $manifestPath -Value $manifest -Encoding UTF8
  foreach($required in @('BuildAI.AccIssueReturn.Revit.dll','BuildAI.AccIssueReturn.Core.dll','Newtonsoft.Json.dll'))
  {if(!(Test-Path (Join-Path $runtimeDir $required))){throw "Revit $v payload is missing $required."}}
  if(!(Test-Path $manifestPath)){throw "Revit $v payload is missing the addin manifest."}
  if($v-ge 2025-and !(Test-Path (Join-Path $runtimeDir 'BuildAI.AccIssueReturn.Revit.deps.json'))){throw "Revit $v payload is missing BuildAI.AccIssueReturn.Revit.deps.json."}
}
$msi=Join-Path $out ("BuildAI_AccIssueReturn_"+$version+"_Setup.msi")
Push-Location (Join-Path $root 'installer')
try{wix build (Join-Path $root 'installer\BuildAI.AccIssueReturn.wxs') -arch x64 -o $msi;if($LASTEXITCODE-ne 0){throw 'WiX build failed.'}}finally{Pop-Location}
if(!(Test-Path $msi)){throw 'Separate MSI was not created.'}
& (Join-Path $root 'tools\Test-Package.ps1') -Root $root -MsiPath $msi
if($LASTEXITCODE-ne 0){throw 'Package verification failed.'}
Write-Host "BuildAI ACC Issue Return MSI: $msi"
& (Join-Path (Split-Path $root -Parent) 'tools/Build-SingleFileBundle.ps1') -InstallerDirectory $installerDir -MsiPath $msi -OutputPath (Join-Path $out ('BuildAI_AccIssueReturn_'+$version+'_Setup.exe')) -Version $version
