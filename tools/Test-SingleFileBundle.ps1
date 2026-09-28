param(
  [Parameter(Mandatory=$true)][string]$ExePath,
  [Parameter(Mandatory=$true)][string]$MsiPath,
  [Parameter(Mandatory=$true)][string]$Version,
  [Parameter(Mandatory=$true)][string]$InstallerDirectory
)
$ErrorActionPreference='Stop'
$ExePath=(Resolve-Path $ExePath).Path
$MsiPath=(Resolve-Path $MsiPath).Path
$siblings=@(Get-ChildItem -LiteralPath (Split-Path $ExePath -Parent) -File | Where-Object {$_.Extension -in @('.bin','.cab','.msi')})
if($siblings.Count){throw 'Bundle output contains external installer payload.'}
$testRoot=Join-Path ([IO.Path]::GetTempPath()) ('BuildAI-BundleTest-'+[guid]::NewGuid().ToString('N'))
$isolated=Join-Path $testRoot 'isolated'
New-Item -ItemType Directory -Path $isolated -Force | Out-Null
$exe=Join-Path $isolated (Split-Path $ExePath -Leaf)
Copy-Item -LiteralPath $ExePath -Destination $exe
$contents=Join-Path $testRoot 'contents'
$ba=Join-Path $testRoot 'ba'
wix burn extract $exe -o $contents -oba $ba
if($LASTEXITCODE -ne 0){throw 'Isolated Bundle extraction failed.'}
$manifest=[xml](Get-Content -LiteralPath (Join-Path $ba 'manifest.xml') -Raw)
$containers=@($manifest.SelectNodes("//*[local-name()='Container']"))
if(!$containers.Count -or @($containers | Where-Object {$_.Attached -ne 'yes'}).Count){throw 'Bundle has a detached container.'}
$payloads=@($manifest.SelectNodes("/*[local-name()='BurnManifest']/*[local-name()='Payload']"))
if(!$payloads.Count -or @($payloads | Where-Object {$_.Packaging -ne 'embedded' -or $_.DownloadUrl}).Count){throw 'Bundle has external or network payload.'}
$packages=@($manifest.SelectNodes("//*[local-name()='Chain']/*[local-name()='MsiPackage']"))
if($packages.Count -ne 1 -or ([version]$packages[0].Version) -ne ([version]$Version)){throw 'Bundle MSI chain/version mismatch.'}
if(([version]$manifest.BurnManifest.Registration.Version) -ne ([version]('{0}.{1}.{2}.{3}' -f ([version]$Version).Major,([version]$Version).Minor,[Math]::Max(0,([version]$Version).Build),[Math]::Max(0,([version]$Version).Revision)))){throw 'Bundle version mismatch.'}
$embedded=@(Get-ChildItem -LiteralPath $contents -Recurse -File -Filter '*.msi')
if($embedded.Count -ne 1 -or (Get-FileHash $embedded[0].FullName).Hash -ne (Get-FileHash $MsiPath).Hash){throw 'Embedded MSI SHA-256 mismatch.'}
& (Join-Path $PSScriptRoot 'Test-InstallerBranding.ps1') -InstallerDirectory $InstallerDirectory -WixSource (Join-Path $InstallerDirectory $(if(Test-Path (Join-Path $InstallerDirectory 'BuildAI.RevitSuite.wxs')){'BuildAI.RevitSuite.wxs'}else{'BuildAI.AccIssueReturn.wxs'})) -BundleSource (Join-Path $InstallerDirectory 'BuildAI.Bundle.wxs') -SetupExe $exe
# Layout launches the isolated EXE and verifies every payload without installing.
$layout=Join-Path $testRoot 'layout'
$log=Join-Path $testRoot 'layout.log'
$process=Start-Process -FilePath $exe -ArgumentList @('/quiet','/layout',('"'+$layout+'"'),'/log',('"'+$log+'"')) -PassThru -WindowStyle Hidden
if(!$process.WaitForExit(60000)){throw 'Isolated Bundle layout timed out.'}
if($process.ExitCode -ne 0){throw "Isolated Bundle layout failed: $($process.ExitCode); $log"}
$laidOut=Join-Path $layout (Split-Path $exe -Leaf)
if(!(Test-Path $laidOut) -or (Get-FileHash $laidOut).Hash -ne (Get-FileHash $exe).Hash){throw 'Isolated EXE layout did not reproduce the self-contained Bundle.'}
Write-Host "Attached Bundle extraction, MSI hash, icon and isolated EXE layout passed: $ExePath"
