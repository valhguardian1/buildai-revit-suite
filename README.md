# BuildAI Revit Plugin Suite 7.4

Source for one BuildAI installer supporting Revit 2023–2026.

## Current behavior

1. Clash Issues highlight both intersecting elements, including elements from different loaded Viewer models.
2. Clash Issues use and prepare only `BuildAI Coordination`.
3. `BuildAI AR-ST` is owned exclusively by the AR-ST comparison workflow.
4. Clash Issue descriptions contain concise engineering context and never exceed Autodesk's 1000-character limit.
5. Camera state is captured from Autodesk Viewer in its native projection schema; clash framing follows the pair overlap.
6. Local Issue logs retain compact LLM-oriented summaries.

See `CHANGELOG.md` for the condensed release history.

## Build

This repository contains BuildAI 7.4 and ACC Issue Return 1.1 source code. Generated installers, binaries, credentials and local build state are excluded from Git. See [README_BUILD.md](README_BUILD.md) for prerequisites and validation commands.

Run PowerShell 7 on Windows from this directory:

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

The default functional build does not use Obfuscar. The MSI is written to:

```text
installer\output\BuildAI_RevitSuite_7.4_Setup.msi
installer\output\BuildAI_RevitPlugins_7.4_Setup.exe
```

The build validates source contracts, compiles payloads for Revit 2023–2026, creates the MSI and EXE installers, and verifies the MSI file table and branding. The separate ACC Issue Return 1.1 installer is built with `acc-issue-return/build.ps1`.

## Runtime files

1. Configuration: `%LOCALAPPDATA%\BuildAI\config.json`
2. Logs: `%LOCALAPPDATA%\BuildAI\logs\`
3. Add-ins: `%ProgramData%\Autodesk\Revit\Addins\<year>\`

Do not copy loose historical patch files into `src`; version 7.0.20 publication behavior is already integrated in `BuildAI.Core`.
