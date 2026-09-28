[CmdletBinding()]
param(
  [Parameter(Mandatory)]
  [ValidateNotNullOrEmpty()]
  [string]$Reason
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$configPath = Join-Path $PSScriptRoot 'harness.json'
$baselinePath = Join-Path $PSScriptRoot 'protected-baseline.json'
$config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json

$files = [ordered]@{}
foreach ($relativePath in $config.protectedFiles) {
  $path = Join-Path $root $relativePath
  if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
    throw "Protected file does not exist: $relativePath"
  }
  $files[$relativePath] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
}

$baseline = [ordered]@{
  schemaVersion = 1
  generatedAtUtc = [DateTime]::UtcNow.ToString('o')
  reason = $Reason
  algorithm = 'SHA256'
  files = $files
}
$baseline | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $baselinePath -Encoding utf8
Write-Host "Protected baseline updated: $baselinePath" -ForegroundColor Green
