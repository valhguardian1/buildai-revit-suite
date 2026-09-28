param(
  [Parameter(Mandatory=$true)][string]$Path,
  [Parameter(Mandatory=$true)][string]$PfxPath,
  [string]$PfxPassword = "",
  [string]$TimestampUrl = "http://timestamp.digicert.com"
)
$ErrorActionPreference = 'Stop'
$signtool = Get-Command signtool.exe -ErrorAction SilentlyContinue
if (-not $signtool) { throw "signtool.exe was not found. Install the Windows SDK." }
if (-not (Test-Path $PfxPath -PathType Leaf)) { throw "PFX not found: $PfxPath" }
$files = @(Get-ChildItem $Path -Recurse -File | Where-Object { $_.Extension -in '.dll','.exe','.msi' })
if ($files.Count -eq 0) { throw "No signable files found under $Path" }
foreach ($file in $files) {
  $args = @('sign','/fd','SHA256','/td','SHA256','/tr',$TimestampUrl,'/f',$PfxPath)
  if ($PfxPassword) { $args += @('/p',$PfxPassword) }
  $args += $file.FullName
  & $signtool @args
  if ($LASTEXITCODE -ne 0) { throw "Signing failed: $($file.FullName)" }
}
Write-Host ("Signed {0} file(s)." -f $files.Count) -ForegroundColor Green
