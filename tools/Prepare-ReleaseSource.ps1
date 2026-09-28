[CmdletBinding()]
param([string]$Destination = '')
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$name = 'BuildAI_Release_Source_7.4_AccIssueReturn_1.1'
if (-not $Destination) { $Destination = Join-Path $root $name }
$destinationFull = [IO.Path]::GetFullPath($Destination)
if (-not $destinationFull.StartsWith(($root.TrimEnd('\') + '\'), [StringComparison]::OrdinalIgnoreCase)) { throw 'Release destination must stay inside the source root.' }
if (Test-Path -LiteralPath $destinationFull) { throw "Release destination already exists: $destinationFull" }
$stage = Join-Path ([IO.Path]::GetTempPath()) ($name + '-' + [guid]::NewGuid().ToString('N'))
if (-not $stage.StartsWith([IO.Path]::GetTempPath(), [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid staging path.' }
New-Item -ItemType Directory -Path $stage | Out-Null

$excludedDirs = @('bin','obj','.git','.vs','.harness','output','payload','verification','node_modules','.wix','packages','__pycache__','.idea')
$excludedFiles = @('.DS_Store','Thumbs.db','desktop.ini')
function Copy-AllowedTree([string]$relative) {
    $from = Join-Path $root $relative
    if (-not (Test-Path -LiteralPath $from -PathType Container)) { throw "Required source directory missing: $relative" }
    Get-ChildItem -LiteralPath $from -Recurse -File -Force | ForEach-Object {
        $tail = $_.FullName.Substring($root.Length).TrimStart('\','/')
        $segments = $tail -split '[\\/]'
        if (@($segments | Where-Object { $_ -in $excludedDirs }).Count -gt 0) { return }
        if ($_.Name -in $excludedFiles -or $_.Name -like '*.log' -or $_.Name -like '*.tmp' -or $_.Name -like '*.bak' -or $_.Name -like '*.zip') { return }
        if ($_.Name -eq '=0' -or $_.Name -like 'RELEASE_NOTES_1.0.*' -or $_.Name -like 'RELEASE_REPORT_*' -or $_.Name -like 'RELEASE_VALIDATION_*' -or $_.Name -eq 'AUDIT_REPORT_1.0.1.md' -or $_.Name -eq 'installer-verification.json') { return }
        $target = Join-Path $stage $tail
        New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
        Copy-Item -LiteralPath $_.FullName -Destination $target
    }
}
function Copy-RequiredFile([string]$relative) {
    $source = Join-Path $root $relative
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Required source file missing: $relative" }
    $target = Join-Path $stage $relative
    New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination $target
}

# Explicit source allowlist. The release keeps the same relative layout as the solutions.
foreach ($dir in @('.config','src','tests','tools','harness','installer','acc-issue-return','design','docs','logo')) { Copy-AllowedTree $dir }
foreach ($file in @('BuildAI.RevitSuite.sln','Directory.Build.props','build.ps1','README.md','CHANGELOG.md','ITERATION.txt','AGENTS.md','Uninstall-All-BuildAI.cmd')) { Copy-RequiredFile $file }

$artifactDir = Join-Path $stage 'Artifacts'
New-Item -ItemType Directory -Path $artifactDir | Out-Null
$installers = @(
    'installer/output/BuildAI_RevitPlugins_7.4_Setup.exe',
    'acc-issue-return/installer/output/BuildAI_AccIssueReturn_1.1_Setup.exe'
)
foreach ($installer in $installers) {
    $source = Join-Path $root $installer
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Final installer missing: $installer" }
    Copy-Item -LiteralPath $source -Destination $artifactDir
}

$textExtensions = @('.cs','.csproj','.props','.targets','.ps1','.cjs','.js','.json','.xml','.xaml','.wxs','.iss','.addin','.config','.md','.txt','.cmd','.sln')
$patterns = [ordered]@{
    'API token literal' = 'sk-or-v1-[A-Za-z0-9]{20,}|sk-[A-Za-z0-9]{32,}'
    'Authorization header' = '(?i)Authorization\s*[:=]\s*["'']?Bearer\s+[A-Za-z0-9._-]{24,}'
    'Password assignment' = '(?i)(password|secret|api[_-]?key)\s*[:=]\s*["''][^"''\s]{16,}["'']'
    'Private key' = '-----BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY-----'
}
$findings = [System.Collections.Generic.List[string]]::new()
Get-ChildItem -LiteralPath $stage -Recurse -File | Where-Object { $_.Extension -in $textExtensions } | ForEach-Object {
    $content = [IO.File]::ReadAllText($_.FullName)
    foreach ($entry in $patterns.GetEnumerator()) {
        if ([regex]::IsMatch($content,$entry.Value)) { $findings.Add("$($_.FullName.Substring($stage.Length + 1)): $($entry.Key)") }
    }
}
if ($findings.Count -gt 0) { throw "Potential secrets found in staged release (values suppressed):`n$($findings -join "`n")" }

$manifest = @'
BuildAI release source 7.4 / ACC Issue Return 1.1
Main plugin: product/installer 7.4, assembly/file 7.4.0.0; Revit 2023-2026.
ACC Issue Return: product/installer 1.1, assembly/file 1.1.0.0; Revit 2023-2026.
Source layout: src and tests (main/shared), acc-issue-return/src and tests, installer, tools, harness, design, logo, docs.
Artifacts: two self-contained WiX Burn EXEs; MSI payloads are embedded.
Branding: logo/APPLY_ICONS_CODEX.md; installer/assets and acc-issue-return/installer/assets.
Secrets: no embedded API keys; optional BUILDAI_ARST_AI_API_KEY and BUILDAI_CLASH_AI_API_KEY environment variables.
'@
Set-Content -LiteralPath (Join-Path $stage 'RELEASE_MANIFEST.txt') -Value $manifest -Encoding utf8
Copy-Item -LiteralPath (Join-Path $root 'tools/README_BUILD_RELEASE.md') -Destination (Join-Path $stage 'README_BUILD.md')
New-Item -ItemType Directory -Path (Join-Path $stage 'Documentation') | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'tools/THIRD_PARTY_LICENSES_RELEASE.md') -Destination (Join-Path $stage 'Documentation/THIRD_PARTY_LICENSES.md')
& (Join-Path $stage 'tools/Update-ReleaseChecksums.ps1') -Root $stage -CopyFreshBuild:$false
$findings.Clear()
Get-ChildItem -LiteralPath $stage -Recurse -File | Where-Object { $_.Extension -in $textExtensions } | ForEach-Object {
    $content = [IO.File]::ReadAllText($_.FullName)
    foreach ($entry in $patterns.GetEnumerator()) {
        if ([regex]::IsMatch($content,$entry.Value)) { $findings.Add("$($_.FullName.Substring($stage.Length + 1)): $($entry.Key)") }
    }
}
if ($findings.Count -gt 0) { throw "Potential secrets found in completed staging (values suppressed):`n$($findings -join "`n")" }

# Only the fully staged result is moved. No source or existing release directory is removed.
Move-Item -LiteralPath $stage -Destination $destinationFull
Write-Host "Release source prepared: $destinationFull"
