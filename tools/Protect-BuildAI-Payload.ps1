param(
  [Parameter(Mandatory=$true)] [string]$PayloadRoot,
  [string[]]$Versions = @('2023','2024','2025','2026')
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$previousDotnetRollForward = [Environment]::GetEnvironmentVariable('DOTNET_ROLL_FORWARD', 'Process')
$modules = @(
  'BuildAI.Core.dll',
  'BuildAI.Plugin1.TimeModelAnalytics.dll',
  'BuildAI.Plugin2.VolumeEstimator.dll',
  'BuildAI.Plugin3.LinkChangeMonitor.dll',
  'BuildAI.Plugin4.LinkComparatorAI.dll',
  'BuildAI.Plugin5.ClashFormaIntegration.dll'
)

Push-Location $root
try {
  # Obfuscar.GlobalTool 2.2.49 targets net9.0. Allow the .NET host to use the
  # installed .NET 10 runtime when .NET 9 is absent, without changing the
  # machine or user environment outside this protection process.
  [Environment]::SetEnvironmentVariable('DOTNET_ROLL_FORWARD', 'Major', 'Process')
  dotnet tool restore
  if ($LASTEXITCODE -ne 0) { throw 'Unable to restore the pinned Obfuscar 2.2.49 tool.' }

  foreach ($version in $Versions) {
    $payload = Join-Path $PayloadRoot $version
    if (-not (Test-Path -LiteralPath $payload -PathType Container)) { continue }
    foreach ($module in $modules) {
      if (-not (Test-Path -LiteralPath (Join-Path $payload $module) -PathType Leaf)) { throw "Missing module for protection: $version / $module" }
    }

    $work = Join-Path $root ("installer\protection\" + $version)
    $input = Join-Path $work 'input'
    $output = Join-Path $work 'output'
    if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $input,$output | Out-Null
    Copy-Item -Path (Join-Path $payload '*') -Destination $input -Recurse -Force

    foreach ($packageName in @('nice3point.revit.api.revitapi','nice3point.revit.api.revitapiui')) {
      $packageRoot = Join-Path $env:USERPROFILE ('.nuget\packages\' + $packageName)
      $reference = Get-ChildItem -LiteralPath $packageRoot -Filter 'RevitAPI*.dll' -File -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match ('\\' + [regex]::Escape($version) + '\.') } |
        Select-Object -First 1
      if ($null -eq $reference) { throw "Revit API reference for $version was not found in $packageRoot" }
      Copy-Item -LiteralPath $reference.FullName -Destination $input -Force
    }

    $config = Join-Path $work 'obfuscar.xml'
    $moduleXml = ($modules | ForEach-Object { '  <Module file="$(InPath)\' + $_ + '" />' }) -join [Environment]::NewLine
    @"
<?xml version="1.0" encoding="utf-8"?>
<Obfuscator>
  <Var name="InPath" value="$input" />
  <Var name="OutPath" value="$output" />
  <Var name="KeepPublicApi" value="true" />
  <Var name="HidePrivateApi" value="true" />
  <Var name="RenameProperties" value="false" />
  <Var name="RenameEvents" value="false" />
  <Var name="UseUnicodeNames" value="false" />
  <Var name="RegenerateDebugInfo" value="false" />
$moduleXml
</Obfuscator>
"@ | Set-Content -LiteralPath $config -Encoding UTF8

    dotnet tool run obfuscar.console -- $config
    if ($LASTEXITCODE -ne 0) { throw "Obfuscar failed for Revit $version" }
    foreach ($module in $modules) {
      $protected = Join-Path $output $module
      if (-not (Test-Path -LiteralPath $protected -PathType Leaf) -or (Get-Item -LiteralPath $protected).Length -eq 0) { throw "Protected module was not produced: $version / $module" }
      Copy-Item -LiteralPath $protected -Destination (Join-Path $payload $module) -Force
    }
  }
}
finally {
  [Environment]::SetEnvironmentVariable('DOTNET_ROLL_FORWARD', $previousDotnetRollForward, 'Process')
  Pop-Location
}
