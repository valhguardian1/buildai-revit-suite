param([string]$UpgradeCode='{B4E3C2F1-6A14-4DA9-9A41-2D6D2D0FCA72')
$roots=@('HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*','HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*')
$app=Get-ItemProperty $roots -ErrorAction SilentlyContinue | Where-Object { $_.UpgradeCode -eq $UpgradeCode -or $_.DisplayName -eq 'BuildAI ACC Issue Return' } | Select-Object -First 1
if($null -eq $app){Write-Error 'BuildAI ACC Issue Return is not installed.';exit 1}
$code=$app.PSChildName
Start-Process msiexec.exe -ArgumentList "/x $code /norestart" -Wait -Verb RunAs