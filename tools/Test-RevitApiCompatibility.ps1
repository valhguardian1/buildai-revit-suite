param(
    [string]$SourceRoot = (Join-Path $PSScriptRoot '..\src')
)

$ErrorActionPreference = 'Stop'
$errors = New-Object System.Collections.Generic.List[string]

$forbiddenPatterns = @(
    @{
        Pattern = '\.IsElementHidden\s*\('
        Message = 'View.IsElementHidden is not available in the supported Revit 2023 API. Use Element.IsHidden(view) instead.'
    },
    @{
        Pattern = 'Where\s*\(\s*view\.IsElementHidden\s*\)'
        Message = 'Method-group use of View.IsElementHidden is incompatible with supported Revit versions.'
    }
)

Get-ChildItem -Path $SourceRoot -Recurse -File -Include *.cs | ForEach-Object {
    $file = $_
    $lines = Get-Content -LiteralPath $file.FullName
    for ($i = 0; $i -lt $lines.Count; $i++) {
        foreach ($rule in $forbiddenPatterns) {
            if ($lines[$i] -match $rule.Pattern) {
                $relative = $file.FullName.Substring((Resolve-Path $SourceRoot).Path.Length).TrimStart([char]'\\', [char]'/')
                $errors.Add(('{0}:{1}: {2}' -f $relative, ($i + 1), $rule.Message))
            }
        }
    }
}

if ($errors.Count -gt 0) {
    Write-Host '[ERROR] Unsupported Revit API references found:' -ForegroundColor Red
    $errors | ForEach-Object { Write-Host ('  ' + $_) -ForegroundColor Red }
    exit 1
}

Write-Host '[OK] No unsupported Revit API references were found.' -ForegroundColor Green
exit 0
