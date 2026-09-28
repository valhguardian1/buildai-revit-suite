$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$forbidden = 'CorrectPushpinsCommand|P5_Pushpins|Btn_CorrectPushpins|PushpinCorrectionService|ApsIssuePushpin|IssuePushpinMarker|PushpinOffsetCalibration|GetIssuesByAutodeskIdAsync|IssueElementRef|ResolvedIssueElement|P5_Pushpin'
$files = Get-ChildItem (Join-Path $root 'src') -Recurse -File | Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' -and $_.Extension -in '.cs','.csproj','.resx','.addin','.json' }
$files += Get-ChildItem (Join-Path $root 'installer') -Recurse -File | Where-Object { $_.Extension -in '.addin','.wxs' }
foreach ($file in $files) {
    if ([IO.File]::ReadAllText($file.FullName) -match $forbidden) { throw "Reverse ACC import remains: $($file.FullName)" }
}
Write-Host '[OK] Reverse ACC Issues command, API, models and localization are absent.'
