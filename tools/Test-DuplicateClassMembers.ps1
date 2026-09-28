param(
    [string]$Root = (Join-Path $PSScriptRoot "..\src")
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $Root)) {
    throw "Source directory was not found: $Root"
}

$errors = New-Object System.Collections.Generic.List[string]
$resolvedRoot = (Resolve-Path $Root).Path
$files = Get-ChildItem -Path $Root -Recurse -File -Filter "*.cs"

foreach ($file in $files) {
    $lines = Get-Content -LiteralPath $file.FullName
    $depth = 0
    $pendingClass = $null
    $classStack = New-Object System.Collections.Generic.List[object]

    for ($lineIndex = 0; $lineIndex -lt $lines.Count; $lineIndex++) {
        $line = $lines[$lineIndex]
        $lineNumber = $lineIndex + 1

        # This guard intentionally targets ordinary class declarations used in this solution.
        if ($line -match '\bclass\s+([A-Za-z_][A-Za-z0-9_]*)') {
            $pendingClass = [pscustomobject]@{
                Name = $Matches[1]
                DeclarationLine = $lineNumber
            }
        }

        $currentClass = if ($classStack.Count -gt 0) { $classStack[$classStack.Count - 1] } else { $null }

        # Only inspect members declared directly inside the current class. Methods are excluded
        # because C# permits overloads; properties and fields with the same name are not permitted.
        if ($null -ne $currentClass -and $depth -eq $currentClass.MemberDepth) {
            $memberName = $null

            # Property (auto-property, block property, or expression-bodied property).
            if ($line -match '^\s*(?:public|private|protected|internal)\s+(?:(?:static|virtual|override|abstract|sealed|new|required|readonly)\s+)*[A-Za-z_][A-Za-z0-9_\.<>\?,\[\]]*\s+([A-Za-z_][A-Za-z0-9_]*)\s*(?:\{|=>)') {
                $memberName = $Matches[1]
            }
            # Field. Excludes methods by requiring =, ;, or , immediately after the name.
            elseif ($line -match '^\s*(?:public|private|protected|internal)\s+(?:(?:static|readonly|const|volatile|new)\s+)*[A-Za-z_][A-Za-z0-9_\.<>\?,\[\]]*\s+([A-Za-z_][A-Za-z0-9_]*)\s*(?:=|;|,)') {
                $memberName = $Matches[1]
            }

            if ($memberName) {
                if ($currentClass.Members.ContainsKey($memberName)) {
                    $relative = $file.FullName.Substring($resolvedRoot.Length).TrimStart([char[]]@('\','/'))
                    $firstLine = $currentClass.Members[$memberName]
                    $errors.Add("${relative}: class '$($currentClass.Name)' declares member '$memberName' more than once (lines $firstLine and $lineNumber).")
                }
                else {
                    $currentClass.Members[$memberName] = $lineNumber
                }
            }
        }

        # Count braces after inspecting the declaration at the current depth.
        $openCount = ([regex]::Matches($line, '\{')).Count
        $closeCount = ([regex]::Matches($line, '\}')).Count

        for ($i = 0; $i -lt $openCount; $i++) {
            $depth++
            if ($null -ne $pendingClass) {
                $classStack.Add([pscustomobject]@{
                    Name = $pendingClass.Name
                    MemberDepth = $depth
                    Members = @{}
                })
                $pendingClass = $null
            }
        }

        for ($i = 0; $i -lt $closeCount; $i++) {
            if ($classStack.Count -gt 0 -and $depth -eq $classStack[$classStack.Count - 1].MemberDepth) {
                $classStack.RemoveAt($classStack.Count - 1)
            }
            $depth--
        }
    }
}

if ($errors.Count -gt 0) {
    Write-Host "[FAILED] Duplicate class members were found:" -ForegroundColor Red
    foreach ($errorText in $errors) {
        Write-Host "  - $errorText" -ForegroundColor Red
    }
    exit 1
}

Write-Host "[OK] No duplicate property or field declarations were found in any class." -ForegroundColor Green
exit 0
