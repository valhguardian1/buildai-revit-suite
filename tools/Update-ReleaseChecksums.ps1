[CmdletBinding()]
param([string]$Root = '', [switch]$CopyFreshBuild)
$ErrorActionPreference = 'Stop'
if (-not $Root) { $Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path }
$Root = (Resolve-Path -LiteralPath $Root).Path
$artifacts = Join-Path $Root 'Artifacts'
New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
if ($CopyFreshBuild) {
    foreach ($relative in @(
        'installer/output/BuildAI_RevitPlugins_7.4_Setup.exe',
        'acc-issue-return/installer/output/BuildAI_AccIssueReturn_1.1_Setup.exe'
    )) {
        $source = Join-Path $Root $relative
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Fresh installer missing: $relative" }
        Copy-Item -LiteralPath $source -Destination $artifacts -Force
    }
    $dlls = @(
        @{ Source = 'installer/payload/2026/BuildAI.Core.dll'; Target = 'DLL/Main/BuildAI.Core.dll' },
        @{ Source = 'installer/payload/2026/BuildAI.Plugin4.LinkComparatorAI.dll'; Target = 'DLL/Main/BuildAI.Plugin4.LinkComparatorAI.dll' },
        @{ Source = 'installer/payload/2026/BuildAI.Plugin5.ClashFormaIntegration.dll'; Target = 'DLL/Main/BuildAI.Plugin5.ClashFormaIntegration.dll' },
        @{ Source = 'acc-issue-return/installer/payload/2026/BuildAI.AccIssueReturn/BuildAI.AccIssueReturn.Core.dll'; Target = 'DLL/AccIssueReturn/BuildAI.AccIssueReturn.Core.dll' },
        @{ Source = 'acc-issue-return/installer/payload/2026/BuildAI.AccIssueReturn/BuildAI.AccIssueReturn.Revit.dll'; Target = 'DLL/AccIssueReturn/BuildAI.AccIssueReturn.Revit.dll' }
    )
    foreach ($dll in $dlls) {
        $source = Join-Path $Root $dll.Source
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Fresh DLL missing: $($dll.Source)" }
        $target = Join-Path (Join-Path $Root 'VerificationBinaries') $dll.Target
        New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
        Copy-Item -LiteralPath $source -Destination $target -Force
    }
}
$hashFiles = @(
    'Artifacts/BuildAI_RevitPlugins_7.4_Setup.exe',
    'Artifacts/BuildAI_AccIssueReturn_1.1_Setup.exe',
    'build.ps1',
    'acc-issue-return/build.ps1',
    'tools/Prepare-ReleaseSource.ps1',
    'tools/Update-ReleaseChecksums.ps1',
    'tools/Verify-ReleaseAutonomy.ps1',
    'tools/Finalize-ReleaseSource.ps1',
    'tools/Test-ReleaseSnapshot.cjs',
    'tools/Test-ReleaseInstallers.ps1',
    'tools/Test-InstallerBranding.ps1',
    'tools/Build-SingleFileBundle.ps1',
    'tools/Test-SingleFileBundle.ps1',
    'tools/Test-ExeIcon.ps1',
    'tools/Test-BundleWindow.ps1'
)
$dllHashFiles = @(
    'VerificationBinaries/DLL/Main/BuildAI.Core.dll',
    'VerificationBinaries/DLL/Main/BuildAI.Plugin4.LinkComparatorAI.dll',
    'VerificationBinaries/DLL/Main/BuildAI.Plugin5.ClashFormaIntegration.dll',
    'VerificationBinaries/DLL/AccIssueReturn/BuildAI.AccIssueReturn.Core.dll',
    'VerificationBinaries/DLL/AccIssueReturn/BuildAI.AccIssueReturn.Revit.dll'
)
$presentDllCount = @($dllHashFiles | Where-Object { Test-Path -LiteralPath (Join-Path $Root $_) }).Count
if ($presentDllCount -ne 0 -and $presentDllCount -ne $dllHashFiles.Count) { throw 'Only some key DLL artifacts are present.' }
if ($presentDllCount -eq $dllHashFiles.Count) { $hashFiles += $dllHashFiles }
$lines = foreach ($relative in $hashFiles) {
    $file = Join-Path $Root $relative
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Checksum input missing: $relative" }
    '{0}  {1}' -f (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant(), $relative
}
$lines | Set-Content -LiteralPath (Join-Path $Root 'SHA256SUMS.txt') -Encoding ascii
Write-Host "Checksums updated: $($hashFiles.Count) files"

$artifactLines = foreach ($file in @(Get-ChildItem -LiteralPath $artifacts -File -Filter '*.exe')) { '{0}  {1}' -f (Get-FileHash $file.FullName).Hash.ToLowerInvariant(), $file.Name }
$artifactLines | Set-Content -LiteralPath (Join-Path $artifacts 'SHA256SUMS.txt') -Encoding ascii

$allowed=@('BuildAI_RevitPlugins_7.4_Setup.exe','BuildAI_AccIssueReturn_1.1_Setup.exe','SHA256SUMS.txt','README_INSTALL.txt')
if(@(Get-ChildItem -LiteralPath $artifacts | Where-Object {$_.PSIsContainer -or $_.Name -notin $allowed}).Count){throw 'Artifacts must contain only the two EXEs, checksums and optional install README.'}
