$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$artifactDir = Join-Path $root 'Artifacts'
$installer = New-Object -ComObject WindowsInstaller.Installer
function Get-MsiValue([string]$file,[string]$query) {
    $db = $installer.GetType().InvokeMember('OpenDatabase','InvokeMethod',$null,$installer,@($file,0))
    $view = $db.GetType().InvokeMember('OpenView','InvokeMethod',$null,$db,@($query))
    $view.GetType().InvokeMember('Execute','InvokeMethod',$null,$view,$null) | Out-Null
    $row = $view.GetType().InvokeMember('Fetch','InvokeMethod',$null,$view,$null)
    $value = if ($null -ne $row) { [string]$row.StringData(1) } else { '' }
    $view.GetType().InvokeMember('Close','InvokeMethod',$null,$view,$null) | Out-Null
    return $value
}
foreach ($item in @(
    @{ Name='BuildAI_RevitSuite_7.4_Setup.msi'; Version='7.4' },
    @{ Name='BuildAI_AccIssueReturn_1.1_Setup.msi'; Version='1.1' }
)) {
    $name = if ($item.Version -eq '7.4') { 'BuildAI_RevitPlugins_7.4_Setup.exe' } else { 'BuildAI_AccIssueReturn_1.1_Setup.exe' }
    $exe = Join-Path $artifactDir $name
    $extract = Join-Path ([IO.Path]::GetTempPath()) ('BuildAI-Inspect-'+[guid]::NewGuid().ToString('N'))
    wix burn extract $exe -o $extract -oba (Join-Path $extract 'ba')
    if ($LASTEXITCODE -ne 0) { throw 'Final Bundle extraction failed.' }
    $file = (Get-ChildItem $extract -Recurse -Filter $item.Name -File | Select-Object -First 1).FullName
    $installerDir = if ($item.Version -eq '7.4') { Join-Path $root 'installer' } else { Join-Path $root 'acc-issue-return/installer' }
    & (Join-Path $PSScriptRoot 'Test-SingleFileBundle.ps1') -ExePath $exe -MsiPath $file -Version $item.Version -InstallerDirectory $installerDir
    $version = Get-MsiValue $file "SELECT ``Value`` FROM ``Property`` WHERE ``Property``='ProductVersion'"
    $icon = Get-MsiValue $file "SELECT ``Value`` FROM ``Property`` WHERE ``Property``='ARPPRODUCTICON'"
    $iconRow = Get-MsiValue $file "SELECT ``Name`` FROM ``Icon`` WHERE ``Name``='BuildAIIcon'"
    if ($version -notmatch ('^' + [regex]::Escape($item.Version) + '(\.0)?$')) { throw "MSI version mismatch: $($item.Name), $version" }
    if ($icon -ne 'BuildAIIcon' -or $iconRow -ne 'BuildAIIcon') { throw "MSI product icon missing: $($item.Name)" }
    Write-Host "$($item.Name): ProductVersion $version, ARPPRODUCTICON $icon"
}
Write-Host 'Both final EXEs, attached payloads, versions and icons verified.'
