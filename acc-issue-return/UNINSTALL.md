# Uninstall BuildAI ACC Issue Return

The MSI is the supported uninstaller and removes every file tracked by its MSI components for Revit 2023, 2024, 2025 and 2026.

Use Windows Settings → Apps → Installed apps → **BuildAI ACC Issue Return** → Uninstall, or run:

```powershell
msiexec.exe /x "BuildAI_AccIssueReturn_1.0.3_Setup.msi"
```

For a silent removal:

```powershell
msiexec.exe /x "BuildAI_AccIssueReturn_1.0.3_Setup.msi" /qn /norestart
```

The MSI keeps its separate UpgradeCode and schedules `RemoveExistingProducts` during major upgrade, so installing a newer version removes files tracked only by the previous version before completing the upgrade. Files created by the user after installation, such as logs under `Desktop\BuildAI Logs`, are not MSI payload files and are intentionally preserved.