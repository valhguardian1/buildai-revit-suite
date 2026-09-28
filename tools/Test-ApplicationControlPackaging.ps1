param(
  [string]$ProjectRoot = (Split-Path $PSScriptRoot -Parent),
  [switch]$RequireValidBuildAiSignature
)

$ErrorActionPreference = 'Stop'

$innoScript = Join-Path $ProjectRoot 'installer\BuildAI.iss'
$wixSource = Join-Path $ProjectRoot 'installer\BuildAI.RevitSuite.wxs'
$payloadRoot = Join-Path $ProjectRoot 'installer\payload'
$outputRoot = Join-Path $ProjectRoot 'installer\output'

foreach ($required in @($innoScript, $wixSource, $payloadRoot)) {
  if (-not (Test-Path -LiteralPath $required)) {
    throw "Application-control packaging audit is missing: $required"
  }
}

$bundleText = Get-Content -LiteralPath (Join-Path $ProjectRoot 'installer/BuildAI.Bundle.wxs') -Raw
if ($bundleText -notmatch 'Compressed="yes"' -or $bundleText -match 'Type="detached"') { throw 'Release must use an attached Burn Bundle.' }

$wixText = Get-Content -LiteralPath $wixSource -Raw
if ($wixText -notmatch '<MediaTemplate\s+[^>]*EmbedCab="yes"') {
  throw 'The MSI must embed its CAB payload instead of distributing loose downloaded DLL files.'
}
if ($wixText -notmatch '<MajorUpgrade\s+[^>]*AllowSameVersionUpgrades="yes"') {
  throw 'The test MSI must be able to replace an earlier package with the same product version.'
}
if ($wixText -match '<CustomAction\b') {
  throw 'Executable MSI custom actions are not allowed in the safe application-control packaging path.'
}

$auditRoots = @($payloadRoot)
if (Test-Path -LiteralPath $outputRoot -PathType Container) { $auditRoots += $outputRoot }

$allFiles = @($auditRoots | ForEach-Object {
  Get-ChildItem -LiteralPath $_ -File -Recurse -Force -ErrorAction Stop
})

$marked = New-Object System.Collections.Generic.List[string]
foreach ($file in $allFiles) {
  $zoneStream = Get-Item -LiteralPath $file.FullName -Stream 'Zone.Identifier' -ErrorAction SilentlyContinue
  if ($null -ne $zoneStream) { $marked.Add($file.FullName) }
}
if ($marked.Count -gt 0) {
  throw "Zone.Identifier remains on files that will be installed or distributed:`n$($marked -join "`n")"
}

$buildAiBinaries = @($allFiles | Where-Object {
  ($_.Extension -in @('.dll', '.exe', '.msi')) -and
  ($_.Name -like 'BuildAI*' -or $_.Name -eq 'BuildAI_RevitPlugins_Setup.msi')
} | Sort-Object FullName -Unique)

$invalidSignatures = New-Object System.Collections.Generic.List[string]
$signatureRows = foreach ($file in $buildAiBinaries) {
  $signature = Get-AuthenticodeSignature -LiteralPath $file.FullName
  if ($signature.Status -ne 'Valid') { $invalidSignatures.Add($file.FullName) }
  [pscustomobject]@{
    File = $file.FullName.Substring($ProjectRoot.Length).TrimStart('\','/')
    Status = [string]$signature.Status
    Publisher = if ($signature.SignerCertificate) { $signature.SignerCertificate.Subject } else { '' }
  }
}

if ($signatureRows.Count -gt 0) {
  $signatureRows | Format-Table -AutoSize | Out-Host
}

if ($RequireValidBuildAiSignature -and $invalidSignatures.Count -gt 0) {
  throw "Signing was requested, but these BuildAI binaries do not have a valid Authenticode signature:`n$($invalidSignatures -join "`n")"
}

if (-not $RequireValidBuildAiSignature -and $invalidSignatures.Count -gt 0) {
  Write-Warning ("{0} BuildAI binary file(s) are unsigned. Zone.Identifier cleanup is verified, but a signature-only WDAC policy can still block them." -f $invalidSignatures.Count)
}

Write-Host ("Application-control packaging audit passed: {0} packaged file(s), 0 Zone.Identifier stream(s), same-version replacement enabled, no MSI executable custom actions." -f $allFiles.Count) -ForegroundColor Green
