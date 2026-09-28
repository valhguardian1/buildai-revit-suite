# BuildAI 7.4 / ACC Issue Return 1.1 — build from this folder

This folder is a standalone source snapshot. Keep the directory layout unchanged. The main solution is `BuildAI.RevitSuite.sln`; the return plugin is in `acc-issue-return/BuildAI.AccIssueReturn.sln`. Shared source is under `src/` and package references are in the project files. There are no `packages.lock.json` files in this project.

## Required tools

- Windows x64, PowerShell 7 (verified with 7.6.6), .NET SDK 10 (verified with 10.0.301), .NET Framework 4.8 targeting pack.
- WiX Toolset CLI 5 on `PATH`, verified with 5.0.2. The local dotnet tool manifest restores optional Obfuscar, which the default functional build does not use.
- WiX Bal extension 5.0.2 (restored automatically by the Bundle builder; NuGet access is needed only on the build machine).
- Node.js for the existing source regression tests (verified with 24.19.0).
- NuGet access for `Nice3point.Revit.Api.RevitAPI` and `RevitAPIUI` for Revit 2023–2026, `Newtonsoft.Json` 13.0.3, and `Microsoft.Web.WebView2` 1.0.2088.41. Autodesk API assemblies are consumed from the referenced NuGet packages for compilation and are not redistributed as separate SDK files in this source snapshot. A working Revit installation is required for interactive smoke tests, not for compilation.

## Clean build

Run in PowerShell 7 from this directory:

```powershell
.\tools\Verify-ReleaseAutonomy.ps1
```

This runs restore and clean for every supported Revit configuration, the main canonical Verify, ACC tests/build, and checksum refresh. Equivalent individual commands are:

```powershell
dotnet tool restore --tool-manifest .\.config\dotnet-tools.json
foreach ($year in 23,24,25,26) {
  dotnet restore .\BuildAI.RevitSuite.sln -p:Configuration="Release R$year"
  dotnet restore .\acc-issue-return\src\BuildAI.AccIssueReturn.Revit\BuildAI.AccIssueReturn.Revit.csproj -p:Configuration="Release R$year"
}
.\harness\Invoke-BuildHarness.ps1 -Mode Verify
.\acc-issue-return\build.ps1
.\tools\Update-ReleaseChecksums.ps1 -CopyFreshBuild
.\tools\Test-ReleaseInstallers.ps1
.\tools\Finalize-ReleaseSource.ps1
node .\tools\Test-ReleaseSnapshot.cjs
```

The harness validates the main code, builds Revit 2023–2026 payloads, and creates `installer/output/BuildAI_RevitSuite_7.4_Setup.msi` plus `installer/output/BuildAI_RevitPlugins_7.4_Setup.exe`. The ACC script runs its unit tests and creates `acc-issue-return/installer/output/BuildAI_AccIssueReturn_1.1_Setup.msi`. The checksum script copies only the two final EXEs into `Artifacts/`, and representative DLLs into `VerificationBinaries/` and updates `SHA256SUMS.txt`. `bin/`, `obj/`, installer `payload/` and `output/` are generated locally; they are not part of the source snapshot.

The one-command script creates a temporary short drive mapping for this directory while it builds. This avoids the Windows/MSBuild path-length limit if the saved release folder is nested deeply. The mapping is removed when the script exits.

After a successful build, run `tools/Test-ReleaseInstallers.ps1` under PowerShell 7, then `tools/Finalize-ReleaseSource.ps1` to record verification and remove only generated directories in this release folder. `node tools/Test-ReleaseSnapshot.cjs` checks every listed hash, required installers, unwanted generated directories, absolute developer source paths and common secret patterns.

The optional AI analysis services read `BUILDAI_ARST_AI_API_KEY` and `BUILDAI_CLASH_AI_API_KEY` from the environment. They are not needed for compilation or geometric comparisons. Set them on the Revit host only if those optional services are used. Never put keys into source files or the release folder.

The BuildAI support link is `https://app.buildai.me/support`. The main general Settings window and standalone return Settings window show it. AR-ST parameters appear in a modal window on each AR-ST Compare attempt.

WiX uses `BuildAI.ico` for the Apps/Programs and Features product icon via `ARPPRODUCTICON`. Both Burn EXEs use `BuildAI_Setup.ico` as its Explorer-visible icon. Windows Explorer normally shows the MSI file type icon for `.msi` files; WiX does not support replacing that shell icon. No Explorer MSI icon hack is used.

`tools/Prepare-ReleaseSource.ps1` recreates the source snapshot via an allowlist and temporary staging directory. It refuses an existing destination and does not delete the working source. The included `logo/APPLY_ICONS_CODEX.md` describes the chosen dark ribbon variant and supplied branding assets.

Interactive Revit launch, browser failure behavior, combined installation, upgrade/downgrade and uninstall isolation require a Windows test machine with Revit 2023–2026. Package tables and source contracts are checked here; those host scenarios must be signed off separately if the test machine is unavailable.

Both EXEs use WiX 5.0.2 default attached containers with Compressed="yes". No adjacent MSI/CAB/BIN is required. Build-SingleFileBundle.ps1 verifies extraction, MSI SHA-256, all ICO resource images, and isolated EXE /layout execution without installation. Intermediate MSI files stay in installer/output.

Legacy main EXE migration: old Inno Setup registrations are detected in HKLM/HKCU, 32/64-bit registry views. Remove the old Inno installation through Windows Installed apps before switching to this Burn/MSI release. Automatic in-place upgrade from Inno is blocked to prevent its old uninstaller from removing MSI-owned files. Existing MSI upgrades retain the original MSI UpgradeCode and MajorUpgrade policy. Bundle repair/uninstall use Windows Installer.

The installer contains no network payload. SDK/NuGet downloads are build-time requirements only. Both bundles include WiX Standard Bootstrapper Application (native code). See docs/licenses for third-party notices.

Release Bundle builds also run Test-BundleWindow.ps1: a separate empty folder receives only the EXE, its layout UI process must create the expected window, and Burn detection must succeed. No input is sent to the window; test processes are terminated without requesting installation. An interactive Windows build session is required for this check. Native Computer Use was unavailable during verification; window creation was verified from process metadata and Burn logs.
