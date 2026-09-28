# Single-file installer verification ? BuildAI 7.4 / ACC Issue Return 1.1

Verified 2026-09-26T21:17:09.916Z.

## Cause and fix

The old main EXE was Inno Setup, not Burn. installer/BuildAI.iss set UseSetupLdr=no, producing Setup-0.bin. The release build no longer invokes Inno. Both products now use WiX CLI/Bal/Util 5.0.2 and BuildAI.Bundle.wxs with Bundle Compressed=yes and MsiPackage Compressed=yes. Actual Burn manifests identify WixAttachedContainer, Attached=yes, Packaging=embedded, and no network payload. No payload/revit version was removed.

Main chain: BuildAI_RevitSuite_7.4_Setup.msi (7.4). Return chain: BuildAI_AccIssueReturn_1.1_Setup.msi (1.1). Both MSI projects, component GUIDs, UpgradeCode, installation paths, addins and plugin functional source are unchanged. New stable Bundle UpgradeCodes are separate from existing MSI UpgradeCodes.

## Artifacts

- BuildAI_RevitPlugins_7.4_Setup.exe: 3983495 bytes; SHA-256 4b249b1ac149a53f623ae290dc21bf219800ab987fb49f23c60179514c1de055
- BuildAI_AccIssueReturn_1.1_Setup.exe: 2241709 bytes; SHA-256 00953e2662fed144ee419574877cef8e7589fe4682b5d7a8b7f64525666c048b

Original MSI hashes (embedded MSI was compared independently during build):
- BuildAI_RevitSuite_7.4_Setup.msi: 2b38498d18bf1d6f21b0ba9a510c12f18873d1f5e4899b8aeeace24b636b9b0e
- BuildAI_AccIssueReturn_1.1_Setup.msi: 2105199c4661af621b4ae95082b2858d62378d139a3239b6779cc85d94c4eb1a

Both EXEs are a few megabytes and successfully build, extract and launch; no size restriction requires detached containers. PE FileVersion/ProductVersion are 7.4.0.0 and 1.1.0.0.

## Actual checks

- Autonomous restore + clean + main/return builds: Revit 2023, 2024, 2025, 2026 passed.
- Main canonical Verify: passed, run 20260926T211347563Z; protected baseline, full build, all regression tests and MSI verification passed.
- Main MSI: 150 components, four installable Revit features; addin/DLL dependency verification passed.
- ACC unit tests: 57 passed; runtime/BAML/relative addin/Upgrade/File-table package checks passed.
- WiX burn extract from an isolated folder containing only each EXE passed; embedded MSI SHA-256 matched build MSI.
- Both EXEs ran /quiet /layout successfully from separate empty folders containing only one EXE; exit 0 and layout EXE SHA-256 matched original.
- Process smoke: both EXEs created their expected Setup windows during full-UI /layout, detection result 0x0; no installation requested. Test processes are stopped without input.
- Return normal startup also created its Setup window without Apply. Main normal startup detected an existing legacy Inno installation and showed the intentional migration block; no missing .bin/MSI/CAB failure.
- Native computer-use helper was unavailable; no visual screenshot inspection was possible. Window creation was verified using process metadata and Burn startup logs.
- Icon: all 10 images from installer/assets/BuildAI_Setup.ico matched RT_ICON resources byte-for-byte in both EXEs. LogoFile uses BuildAI.png. Both MSI Icon tables and ARPPRODUCTICON=BuildAIIcon verified. Actual installed Programs and Features appearance was not tested.
- Payload scan: 168 files; no matching token/Authorization/private-key patterns or user logs/dumps. Source snapshot scanner also runs during finalization. Pattern scanning is not a proof against all possible secrets.
- Independent read-only review completed; signing order and legacy Inno migration findings resolved.

## Installation behavior and limits

Actual install, MSI upgrade, downgrade, repair, uninstall and combined-install removal isolation were NOT executed: the user confirmed that no safe test environment is available. The build machine was not modified by an installation. Existing MSI Upgrade tables and downgrade policy were verified statically. Non-administrator behavior was not tested; packages remain perMachine.

Legacy Inno EXE upgrades require removing the old Inno product first via Windows Installed apps. The Bundle searches HKLM/HKCU in both registry views and blocks an unsafe direct transition. Automatic in-place Inno-to-MSI migration is not implemented. Existing MSI upgrades retain their prior UpgradeCode. Do not claim the legacy Inno upgrade criterion as passed.

Installers are unsigned because no signing certificate was supplied. The optional signing path signs MSI before packaging, then Burn engine and final Bundle using WiX detach/reattach; signed output receives the same extraction/hash/icon/startup tests. Signed-path execution was not tested without a certificate.

## Rebuild

Run from this autonomous folder in an interactive Windows PowerShell session:

```powershell
.\tools\Verify-ReleaseAutonomy.ps1
.\tools\Test-ReleaseInstallers.ps1
.\tools\Finalize-ReleaseSource.ps1
node .\tools\Test-ReleaseSnapshot.cjs
```

Build tools and NuGet may access the network during restore; installation payloads are local and embedded. SDK and license instructions are in README_BUILD.md. Artifacts contains only the two EXEs, SHA256SUMS.txt and README_INSTALL.txt. VerificationBinaries contains representative DLLs outside the user installer folder.

## Changed files

installer/BuildAI.Bundle.wxs; acc-issue-return/installer/BuildAI.Bundle.wxs; acc-issue-return/installer/assets/BuildAI_Setup.ico and BuildAI.png; build.ps1; acc-issue-return/build.ps1; tools/Build-SingleFileBundle.ps1; tools/Test-SingleFileBundle.ps1; tools/Test-BundleWindow.ps1; tools/Test-ExeIcon.ps1; tools/Test-InstallerBranding.ps1; tools/Test-ApplicationControlPackaging.ps1; tools/Test-ReleaseInstallers.ps1; tools/Prepare-ReleaseSource.ps1; tools/Update-ReleaseChecksums.ps1; tools/Finalize-ReleaseSource.ps1; tools/Test-ReleaseSnapshot.cjs; tools/README_BUILD_RELEASE.md; tools/THIRD_PARTY_LICENSES_RELEASE.md; docs/licenses/WiX-5.0.2-LICENSE.txt; release README_BUILD.md, RELEASE_MANIFEST.txt, SHA256SUMS.txt, Artifacts and Documentation.

Additional pipeline changes: harness/Invoke-BuildHarness.ps1 publishes only the main EXE and SHA in a new per-version directory; it preserves existing releases and working outputs. Two-part product version bump 7.4 -> 7.4.1 was tested in staging; filename and Assembly/File versions updated correctly. Bundle versions are passed from build parameters, normalized to four components.

References: https://jrsoftware.org/ishelp/topic_setup_usesetupldr.htm ; https://docs.firegiant.com/wix/schema/wxs/bundle/ ; https://docs.firegiant.com/wix/schema/wxs/msipackage/ .
