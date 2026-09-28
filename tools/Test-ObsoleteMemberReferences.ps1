$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$src = Join-Path $root 'src'
$errors = New-Object System.Collections.Generic.List[string]

$forbidden = @(
    @{ Pattern = '\bChangeTypeRu\b'; Message = "Obsolete member 'ChangeTypeRu' found. Use 'ChangeTypeText'." },
    @{ Pattern = 'VEDOMOST IZMENENIY|SVYaZANN'; Message = 'Transliterated Russian UI/export text found. Use English text.' }
)

Get-ChildItem -Path $src -Recurse -File -Include *.cs,*.xaml | ForEach-Object {
    $relative = $_.FullName.Substring($root.Length).TrimStart([char[]]@('\','/'))
    $lines = Get-Content -LiteralPath $_.FullName
    for ($i = 0; $i -lt $lines.Count; $i++) {
        foreach ($rule in $forbidden) {
            if ($lines[$i] -match $rule.Pattern) {
                $errors.Add("${relative}:$($i + 1): $($rule.Message)")
            }
        }
    }
}

if ($errors.Count -gt 0) {
    Write-Host '[FAIL] Obsolete member/text references were found:' -ForegroundColor Red
    $errors | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    exit 1
}

Write-Host '[OK] No obsolete member or transliterated Russian references were found.' -ForegroundColor Green
exit 0
