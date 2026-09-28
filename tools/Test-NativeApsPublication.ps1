$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$src = Join-Path $root 'src'
$forbidden = @('revit_aps_publish','/api/revit_aps_id/','GetPushpinContextAsync')
$errors = New-Object System.Collections.Generic.List[string]
Get-ChildItem -Path $src -Recurse -File -Include *.cs | ForEach-Object {
    $path = $_.FullName
    $text = Get-Content -LiteralPath $path -Raw
    foreach ($token in $forbidden) {
        if ($text.IndexOf($token, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $errors.Add("$path contains forbidden legacy publication reference: $token")
        }
    }
}
$client = Join-Path $src 'BuildAI.Core\APS\ApsPublicationClient.cs'
if (-not (Test-Path $client)) { $errors.Add('Native APS publication client is missing.') }
if ($errors.Count -gt 0) {
    $errors | ForEach-Object { Write-Host "[ERROR] $_" -ForegroundColor Red }
    exit 1
}
Write-Host '[OK] Native APS publication is used; no BuildAI publication endpoints remain.' -ForegroundColor Green
