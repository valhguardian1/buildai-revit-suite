$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$shortDrive = $null
foreach ($letter in @('Z','Y','X','W','V','U','T','S','R')) {
    if (-not (Get-PSDrive -Name $letter -ErrorAction SilentlyContinue)) { $shortDrive = "${letter}:"; break }
}
if (-not $shortDrive) { throw 'No free temporary drive letter for long-path-safe build.' }
subst $shortDrive $root
if ($LASTEXITCODE -ne 0) { throw 'Could not create temporary short build path with subst.' }
$root = "$shortDrive\"
Push-Location $root
try {
    dotnet tool restore --tool-manifest '.\.config\dotnet-tools.json'
    if ($LASTEXITCODE -ne 0) { throw 'dotnet tool restore failed.' }
    foreach ($year in 23,24,25,26) {
        $configuration = "Release R$year"
        dotnet restore '.\BuildAI.RevitSuite.sln' -p:Configuration="$configuration"
        if ($LASTEXITCODE -ne 0) { throw "Main restore failed for $configuration." }
        dotnet restore '.\acc-issue-return\src\BuildAI.AccIssueReturn.Revit\BuildAI.AccIssueReturn.Revit.csproj' -p:Configuration="$configuration"
        if ($LASTEXITCODE -ne 0) { throw "ACC restore failed for $configuration." }
        dotnet clean '.\BuildAI.RevitSuite.sln' -c $configuration
        if ($LASTEXITCODE -ne 0) { throw "Main clean failed for $configuration." }
        dotnet clean '.\acc-issue-return\src\BuildAI.AccIssueReturn.Revit\BuildAI.AccIssueReturn.Revit.csproj' -c $configuration
        if ($LASTEXITCODE -ne 0) { throw "ACC clean failed for $configuration." }
    }
    & '.\harness\Invoke-BuildHarness.ps1' -Mode Verify
    if ($LASTEXITCODE -ne 0) { throw 'Main harness Verify failed.' }
    & '.\acc-issue-return\build.ps1'
    if ($LASTEXITCODE -ne 0) { throw 'ACC build failed.' }
    & '.\tools\Update-ReleaseChecksums.ps1' -Root $root -CopyFreshBuild
    Write-Host 'Autonomous restore, clean, build, test, installer and checksum verification passed.'
}
finally {
    Pop-Location
    subst $shortDrive /D
}
