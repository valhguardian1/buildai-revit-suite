param(
    [switch]$RequireSigningTools
)

$ErrorActionPreference = 'Stop'

Write-Host 'BuildAI Revit Suite - build prerequisite check' -ForegroundColor Cyan
$failed = $false

function Test-RequiredCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [string]$HelpText
    )

    if (Get-Command $Name -ErrorAction SilentlyContinue) {
        Write-Host "[OK] $Name" -ForegroundColor Green
        return
    }

    Write-Host "[MISSING] $Name - $HelpText" -ForegroundColor Red
    $script:failed = $true
}

if (-not $IsWindows) {
    Write-Host '[MISSING] Windows x64 build environment is required for WiX, IExpress and Authenticode verification.' -ForegroundColor Red
    $failed = $true
}

if ($PSVersionTable.PSVersion.Major -lt 7) {
    Write-Host "[MISSING] PowerShell 7 or later is required. Current: $($PSVersionTable.PSVersion)" -ForegroundColor Red
    $failed = $true
}
else {
    Write-Host "[OK] PowerShell $($PSVersionTable.PSVersion)" -ForegroundColor Green
}

Test-RequiredCommand -Name 'dotnet' -HelpText 'Install the .NET SDK used by the project.'
Test-RequiredCommand -Name 'wix' -HelpText 'Install WiX 5: dotnet tool install --global wix --version 5.0.2'

$iexpress = if ($env:WINDIR) { Join-Path $env:WINDIR 'System32\iexpress.exe' } else { $null }
if ($iexpress -and (Test-Path -LiteralPath $iexpress -PathType Leaf)) {
    Write-Host "[OK] IExpress: $iexpress" -ForegroundColor Green
}
else {
    Write-Host '[MISSING] IExpress is required to create BuildAI_Full_Cleanup.exe.' -ForegroundColor Red
    $failed = $true
}

$net48Reference = if (${env:ProgramFiles(x86)}) {
    Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8\mscorlib.dll'
}
else {
    $null
}
if ($net48Reference -and (Test-Path -LiteralPath $net48Reference -PathType Leaf)) {
    Write-Host '[OK] .NET Framework 4.8 targeting pack' -ForegroundColor Green
}
else {
    Write-Host '[MISSING] .NET Framework 4.8 Developer Pack / targeting pack is required for Revit 2023-2024.' -ForegroundColor Red
    $failed = $true
}

if (Get-Command 'dotnet' -ErrorAction SilentlyContinue) {
    try {
        $sdkVersion = (& dotnet --version 2>$null | Select-Object -First 1).Trim()
        if ([string]::IsNullOrWhiteSpace($sdkVersion)) { throw 'dotnet --version returned no value.' }
        Write-Host "[OK] .NET SDK $sdkVersion" -ForegroundColor Green
    }
    catch {
        Write-Host "[MISSING] Unable to run the .NET SDK: $($_.Exception.Message)" -ForegroundColor Red
        $failed = $true
    }
}

if (Get-Command 'wix' -ErrorAction SilentlyContinue) {
    try {
        $wixVersion = (& wix --version 2>$null | Select-Object -First 1).Trim()
        $wixMajor = 0
        if (-not [int]::TryParse(($wixVersion -split '\.')[0], [ref]$wixMajor) -or $wixMajor -ne 5) {
            Write-Host "[MISSING] WiX 5.x is required; found '$wixVersion'." -ForegroundColor Red
            $failed = $true
        }
        else {
            Write-Host "[OK] WiX $wixVersion" -ForegroundColor Green
        }
    }
    catch {
        Write-Host "[MISSING] Unable to run WiX: $($_.Exception.Message)" -ForegroundColor Red
        $failed = $true
    }
}

if ($RequireSigningTools) {
    Test-RequiredCommand -Name 'signtool.exe' -HelpText 'Install the Windows SDK or omit -SigningPfxPath.'
}

if ($failed) {
    throw 'Required build prerequisites are missing.'
}

Write-Host 'Required one-command build tools are available.' -ForegroundColor Green
