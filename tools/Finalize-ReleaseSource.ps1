$ErrorActionPreference = 'Stop'
$originalRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if ((Split-Path $originalRoot -Leaf) -ne 'BuildAI_Release_Source_7.4_AccIssueReturn_1.1') { throw 'Run this script only inside the release-source folder.' }
$summaryPath = Join-Path $originalRoot '.harness/build-summary.json'
$reportPath = Join-Path $originalRoot 'Documentation/BUILD_VERIFICATION.txt'
if (Test-Path -LiteralPath $summaryPath) {
    $summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
    if ($summary.status -ne 'passed') { throw 'Autonomous harness Verify has not passed.' }
}
elseif (-not (Test-Path -LiteralPath $reportPath) -or -not ((Get-Content -LiteralPath $reportPath -Raw).Contains('Main canonical Verify: passed'))) {
    throw 'Autonomous harness Verify has not passed.'
}
$artifacts = Join-Path $originalRoot 'Artifacts'
$files = @('BuildAI_RevitPlugins_7.4_Setup.exe','BuildAI_AccIssueReturn_1.1_Setup.exe')
foreach ($file in $files) { if (-not (Test-Path -LiteralPath (Join-Path $artifacts $file))) { throw "Release artifact missing: $file" } }
$mainDll = (Get-Item -LiteralPath (Join-Path $originalRoot 'VerificationBinaries/DLL/Main/BuildAI.Core.dll')).VersionInfo
$returnDll = (Get-Item -LiteralPath (Join-Path $originalRoot 'VerificationBinaries/DLL/AccIssueReturn/BuildAI.AccIssueReturn.Revit.dll')).VersionInfo
if ($mainDll.FileVersion -ne '7.4.0.0' -or $returnDll.FileVersion -ne '1.1.0.0') { throw 'Compiled DLL versions are incorrect.' }

$report = @(
    'BuildAI 7.4 / ACC Issue Return 1.1 autonomous build verification',
    "UTC: $([DateTime]::UtcNow.ToString('o'))",
    "Main canonical Verify: $($summary.status); run $($summary.runId)",
    'Main and ACC restore/clean/build: Revit 2023, 2024, 2025, 2026',
    'ACC build runs its unit tests and MSI package/BAML/upgrade table checks.',
    'Main MSI file inventory and all four Revit features checked by canonical Verify.',
    "Main DLL FileVersion: $($mainDll.FileVersion)",
    "ACC DLL FileVersion: $($returnDll.FileVersion)",
    'Both attached Burn EXEs: extraction, embedded MSI SHA-256, all ICO resource images and isolated /layout execution passed.',
    'Run tools/Test-ReleaseInstallers.ps1 under PowerShell 7 for final artifact ProductVersion and icon inspection.',
    'Revit launch, browser failure, actual install/upgrade/downgrade/uninstall require host smoke testing.',
    'Main and ACC binaries/installers are unsigned.'
)
if ($summary) { Set-Content -LiteralPath $reportPath -Value $report -Encoding utf8 }

$textExtensions = @('.cs','.csproj','.props','.targets','.ps1','.cjs','.js','.json','.xml','.xaml','.wxs','.iss','.addin','.config','.md','.txt','.cmd','.sln')
$sensitive = [ordered]@{
    'API token literal' = 'sk-or-v1-[A-Za-z0-9]{20,}|sk-[A-Za-z0-9]{32,}'
    'Authorization header' = '(?i)Authorization\s*[:=]\s*["'']?Bearer\s+[A-Za-z0-9._-]{24,}'
    'Password assignment' = '(?i)(password|secret|api[_-]?key)\s*[:=]\s*["''][^"''\s]{16,}["'']'
    'Private key' = '-----BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY-----'
}
$hits = [System.Collections.Generic.List[string]]::new()
Get-ChildItem -LiteralPath $originalRoot -Recurse -File -Force | Where-Object { $_.Extension -in $textExtensions -and $_.FullName -notmatch '[\\/](bin|obj|payload|output|\.harness)[\\/]' } | ForEach-Object {
    $content = [IO.File]::ReadAllText($_.FullName)
    foreach ($pattern in $sensitive.GetEnumerator()) {
        if ([regex]::IsMatch($content,$pattern.Value)) { $hits.Add("$($_.FullName.Substring($originalRoot.Length + 1)): $($pattern.Key)") }
    }
}
if ($hits.Count -gt 0) { throw "Potential secrets found (values suppressed):`n$($hits -join "`n")" }

# A temporary drive keeps deletion below MAX_PATH. Only explicit generated
# directories inside this release root are removed; source and Artifacts stay.
$drive = $null
foreach ($letter in @('Z','Y','X','W','V','U','T','S','R')) {
    if (-not (Get-PSDrive -Name $letter -ErrorAction SilentlyContinue)) { $drive = "${letter}:"; break }
}
if (-not $drive) { throw 'No free temporary drive letter for release cleanup.' }
subst $drive $originalRoot
if ($LASTEXITCODE -ne 0) { throw 'Could not create temporary cleanup path.' }
try {
    $shortRoot = "$drive\"
    $targets = [System.Collections.Generic.List[string]]::new()
    foreach ($base in @('src','tests','acc-issue-return/src','acc-issue-return/tests')) {
        Get-ChildItem -LiteralPath (Join-Path $shortRoot $base) -Recurse -Directory -Force |
            Where-Object { $_.Name -in @('bin','obj') } |
            ForEach-Object { $targets.Add($_.FullName) }
    }
    foreach ($relative in @('.wix','.harness','installer/payload','installer/output','acc-issue-return/installer/payload','acc-issue-return/installer/output')) {
        $path = Join-Path $shortRoot $relative
        if (Test-Path -LiteralPath $path) { $targets.Add($path) }
    }
    foreach ($target in ($targets | Sort-Object -Unique | Sort-Object Length -Descending)) {
        $full = [IO.Path]::GetFullPath($target)
        if (-not $full.StartsWith($shortRoot, [StringComparison]::OrdinalIgnoreCase)) { throw "Unsafe cleanup target: $full" }
        Remove-Item -LiteralPath $full -Recurse -Force
    }
}
finally { subst $drive /D }

& (Join-Path $originalRoot 'tools/Update-ReleaseChecksums.ps1') -Root $originalRoot -CopyFreshBuild:$false
Write-Host 'Release source finalized: secrets checked, generated directories removed, checksums refreshed.'
