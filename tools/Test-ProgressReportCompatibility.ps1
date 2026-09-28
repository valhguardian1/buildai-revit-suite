$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$errors = @()
Get-ChildItem (Join-Path $root 'src') -Recurse -Filter '*.cs' | ForEach-Object {
    $text = Get-Content $_.FullName -Raw
    $matches = [regex]::Matches($text, '(?m)^\s*(?:var|Progress<[^>]+>)\s+(?<name>\w+)\s*=\s*new\s+Progress<[^>]+>\s*\(')
    foreach ($match in $matches) {
        $name = [regex]::Escape($match.Groups['name'].Value)
        if ([regex]::IsMatch($text, "\b$name\s*\.\s*Report\s*\(")) {
            $errors += "$($_.FullName): concrete Progress<T> variable '$($match.Groups['name'].Value)' calls Report(). Declare it as IProgress<T>."
        }
    }
}
if ($errors.Count -gt 0) {
    $errors | ForEach-Object { Write-Host $_ -ForegroundColor Red }
    exit 1
}
Write-Host '[OK] All direct Report calls use IProgress<T>-compatible declarations.' -ForegroundColor Green
