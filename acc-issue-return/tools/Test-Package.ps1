param([Parameter(Mandatory=$true)][string]$Root,[string]$MsiPath)
$ErrorActionPreference='Stop'
foreach($year in 2023,2024,2025,2026)
{
  $dir=Join-Path $Root "installer\payload\$year"
  $runtimeDir=Join-Path $dir 'BuildAI.AccIssueReturn'
  foreach($name in @('BuildAI.AccIssueReturn.Revit.dll','BuildAI.AccIssueReturn.Core.dll','Newtonsoft.Json.dll'))
  {if(!(Test-Path (Join-Path $runtimeDir $name))){throw "Payload $year is missing $name."}}
  if($year-ge 2025-and !(Test-Path (Join-Path $runtimeDir 'BuildAI.AccIssueReturn.Revit.deps.json'))){throw "Payload $year is missing the net8 dependency manifest."}
  $manifest=[xml](Get-Content -Raw (Join-Path $dir 'BuildAI.AccIssueReturn.addin'))
  if($manifest.RevitAddIns.AddIn.Assembly-ne 'BuildAI.AccIssueReturn\BuildAI.AccIssueReturn.Revit.dll'){throw "Payload $year addin Assembly path is not isolated and relative."}
  & (Get-Command pwsh).Source -NoProfile -File (Join-Path $Root 'tools/Test-BamlResource.ps1') -DllPath (Join-Path $runtimeDir 'BuildAI.AccIssueReturn.Revit.dll')
  if($LASTEXITCODE-ne 0){throw "Payload $year DLL failed the WPF BAML resource check."}
}
if([string]::IsNullOrWhiteSpace($MsiPath)){return}
if(!(Test-Path $MsiPath)){throw "MSI not found: $MsiPath"}
$installer=New-Object -ComObject WindowsInstaller.Installer
$db=$installer.GetType().InvokeMember('OpenDatabase','InvokeMethod',$null,$installer,@($MsiPath,0))
function Read-Rows([string]$sql,[int]$fields)
{
  $view=$db.GetType().InvokeMember('OpenView','InvokeMethod',$null,$db,@($sql));$view.GetType().InvokeMember('Execute','InvokeMethod',$null,$view,$null)|Out-Null;$rows=@()
  while($true){$record=$view.GetType().InvokeMember('Fetch','InvokeMethod',$null,$view,$null);if($null-eq $record){break};$row=@();for($i=1;$i-le $fields;$i++){$row+=$record.StringData($i)};$rows+=,$row}
  $view.GetType().InvokeMember('Close','InvokeMethod',$null,$view,$null)|Out-Null;return ,$rows
}
$productVersion=[string](Read-Rows 'SELECT `Value` FROM `Property` WHERE `Property`=''ProductVersion''' 1)[0]
$arpIcon=[string](Read-Rows 'SELECT `Value` FROM `Property` WHERE `Property`=''ARPPRODUCTICON''' 1)[0]
$iconNames=@(Read-Rows 'SELECT `Name` FROM `Icon`' 1 | ForEach-Object {[string]$_[0]})
if($arpIcon-ne 'BuildAIIcon' -or 'BuildAIIcon' -notin $iconNames){throw 'Built ACC MSI is missing the BuildAI product icon or ARPPRODUCTICON.'}
$upgrade=Read-Rows 'SELECT `UpgradeCode`, `ActionProperty`, `VersionMin`, `VersionMax`, `Attributes` FROM `Upgrade`' 5
$expectedUpgradeCode='{B4E3C2F1-6A14-4DA9-9A41-2D6D2D0FCA72}'
$previous=$upgrade|Where-Object{$_[0]-eq $expectedUpgradeCode-and $_[1]-eq 'WIX_UPGRADE_DETECTED'}|Select-Object -First 1
if($null-eq $previous-or $previous[2]-ne ''-or $previous[3]-ne $productVersion-or (([int]$previous[4])-band 512)-eq 0){throw 'MSI Upgrade row does not detect previous and same versions through an inclusive VersionMax.'}
$newer=$upgrade|Where-Object{$_[0]-eq $expectedUpgradeCode-and $_[1]-eq 'WIX_DOWNGRADE_DETECTED'}|Select-Object -First 1
if($null-eq $newer-or $newer[2]-ne $productVersion-or $newer[3]-ne ''-or (([int]$newer[4])-band 2)-eq 0){throw 'MSI Upgrade row does not detect newer versions with OnlyDetect.'}
$launch=@(Read-Rows 'SELECT `Condition` FROM `LaunchCondition`' 1)
if(@($launch|Where-Object{[string]$_ -match 'WIX_DOWNGRADE_DETECTED'}).Count -gt 0){throw 'MSI still uses a manual downgrade LaunchCondition.'}
$files=Read-Rows 'SELECT `FileName` FROM `File`' 1
foreach($required in @('BuildAI.AccIssueReturn.Revit.dll','BuildAI.AccIssueReturn.Core.dll','BuildAI.AccIssueReturn.Revit.deps.json'))
{if(-not($files|Where-Object{$_[0]-like "*$required*"})){throw "MSI File table is missing $required."}}
Write-Host "Package verification passed: runtime files, BAML resource, relative addin paths, Upgrade/File tables."
