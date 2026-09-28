param(
    [string]$Root = (Join-Path $PSScriptRoot "..\src")
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $Root)) {
    throw "Source directory was not found: $Root"
}

$uiNames = @(
    "TextBox",
    "Button",
    "ComboBox",
    "Image",
    "Panel",
    "Window"
)

$errors = New-Object System.Collections.Generic.List[string]
$files = Get-ChildItem -Path $Root -Recurse -File -Filter "*.cs"

foreach ($file in $files) {
    $content = Get-Content -LiteralPath $file.FullName -Raw

    $importsRevitUi = $content -match '(?m)^\s*using\s+Autodesk\.Revit\.UI\s*;'
    $importsWpfControls = $content -match '(?m)^\s*using\s+System\.Windows\.Controls\s*;'

    if (-not ($importsRevitUi -and $importsWpfControls)) {
        continue
    }

    foreach ($name in $uiNames) {
        $wpfNamespace = if ($name -eq "Window") { "System.Windows" } else { "System.Windows.Controls" }
        $escapedWpfType = [regex]::Escape($wpfNamespace + "." + $name)
        $hasAlias = $content -match ("(?m)^\s*using\s+" + [regex]::Escape($name) + "\s*=\s*" + $escapedWpfType + "\s*;")
        $hasWpfAlias = $content -match ("(?m)^\s*using\s+Wpf" + [regex]::Escape($name) + "\s*=\s*" + $escapedWpfType + "\s*;")
        $usesBareName = $content -match ("(?<![\.\w])" + [regex]::Escape($name) + "(?![\w])")

        if ($usesBareName -and -not ($hasAlias -or $hasWpfAlias)) {
            $relative = $file.FullName.Substring((Resolve-Path $Root).Path.Length).TrimStart([char[]]@('\','/'))
            $errors.Add("$relative uses ambiguous UI type '$name' while importing both Autodesk.Revit.UI and System.Windows.Controls.")
        }
    }
}

if ($errors.Count -gt 0) {
    Write-Host "[FAILED] Ambiguous Revit/WPF UI type references were found:" -ForegroundColor Red
    foreach ($errorText in $errors) {
        Write-Host "  - $errorText" -ForegroundColor Red
    }
    Write-Host "Use an explicit WPF alias, for example:" -ForegroundColor Yellow
    Write-Host "  using WpfTextBox = System.Windows.Controls.TextBox;" -ForegroundColor Yellow
    exit 1
}

Write-Host "[OK] No ambiguous Revit/WPF UI type references were found." -ForegroundColor Green
exit 0
