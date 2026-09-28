param(
  [Parameter(Mandatory=$true)][string]$OutputPath
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$cmd = Join-Path $PSScriptRoot 'Uninstall-All-BuildAI.cmd'
$vbs = Join-Path $PSScriptRoot 'Uninstall-All-BuildAI-Launcher.vbs'
$iexpress = Join-Path $env:WINDIR 'System32\iexpress.exe'

foreach ($required in @($cmd, $vbs, $iexpress)) {
  if (-not (Test-Path $required -PathType Leaf)) { throw "Required file not found: $required" }
}

$outDir = Split-Path $OutputPath -Parent
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
if (Test-Path $OutputPath) { Remove-Item $OutputPath -Force }

$work = Join-Path $env:TEMP ("BuildAI-Uninstaller-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $work | Out-Null
try {
  Copy-Item $cmd (Join-Path $work 'Uninstall-All-BuildAI.cmd') -Force
  Copy-Item $vbs (Join-Path $work 'Uninstall-All-BuildAI-Launcher.vbs') -Force

  $sed = Join-Path $work 'BuildAI-Uninstaller.sed'
  # Guard against the two copies of the cleanup script drifting apart. They did:
# one logged to %TEMP%, the other to the Desktop, and only the tools\ copy was
# ever shipped, so a fix applied to the root copy silently never reached users.
$rootCmd = Join-Path $root 'Uninstall-All-BuildAI.cmd'
if (Test-Path $rootCmd -PathType Leaf) {
  $a = (Get-FileHash $cmd -Algorithm SHA256).Hash
  $b = (Get-FileHash $rootCmd -Algorithm SHA256).Hash
  if ($a -ne $b) {
    throw "Uninstall-All-BuildAI.cmd differs between tools\ and the repository root. " +
          "Only the tools\ copy is packaged, so the root copy is misleading. Synchronise them or delete the root copy."
  }
}

  $sedText = @"
[Version]
Class=IEXPRESS
SEDVersion=3
[Options]
PackagePurpose=InstallApp
ShowInstallProgramWindow=1
HideExtractAnimation=1
UseLongFileName=1
InsideCompressed=0
CAB_FixedSize=0
CAB_ResvCodeSigning=0
RebootMode=N
InstallPrompt=
DisplayLicense=
FinishMessage=
TargetName=$OutputPath
FriendlyName=BuildAI Revit Plugins Cleanup
AppLaunched=wscript.exe Uninstall-All-BuildAI-Launcher.vbs
PostInstallCmd=<None>
AdminQuietInstCmd=wscript.exe Uninstall-All-BuildAI-Launcher.vbs
UserQuietInstCmd=wscript.exe Uninstall-All-BuildAI-Launcher.vbs
SourceFiles=SourceFiles
[SourceFiles]
SourceFiles0=$work\
[SourceFiles0]
%FILE0%=Uninstall-All-BuildAI.cmd
%FILE1%=Uninstall-All-BuildAI-Launcher.vbs
[Strings]
FILE0=Uninstall-All-BuildAI.cmd
FILE1=Uninstall-All-BuildAI-Launcher.vbs
"@
  [System.IO.File]::WriteAllText($sed, $sedText, [System.Text.Encoding]::ASCII)

  $p = Start-Process -FilePath $iexpress -ArgumentList @('/N', $sed) -WindowStyle Hidden -Wait -PassThru
  if ($p.ExitCode -ne 0 -or -not (Test-Path $OutputPath)) {
    throw "IExpress failed to create the standalone uninstaller. Exit code: $($p.ExitCode)"
  }

  Write-Host "Standalone uninstaller created: $OutputPath" -ForegroundColor Green
}
finally {
  if (-not [IO.Path]::GetFullPath($work).StartsWith([IO.Path]::GetFullPath($env:TEMP).TrimEnd('\') + '\BuildAI-Uninstaller-', [StringComparison]::OrdinalIgnoreCase)) { throw "Invalid cleanup path: $work" }
  if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
}
