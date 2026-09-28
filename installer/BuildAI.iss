; ============================================================================
;  BuildAI Revit Plugins - Inno Setup script
;  Same engine as the original BuildAI_2024.exe / BuildAI_2025.exe installers.
;
;  Compile with Inno Setup 6 (https://jrsoftware.org/isdl.php):
;     "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\BuildAI.iss
;  or open this file in the Inno Setup Compiler and press F9.
;
;  REQUIRES the staged payload produced by build.ps1:
;     installer\payload\2023\   installer\payload\2024\   installer\payload\2025\   installer\payload\2026\
;  Versions whose payload folder is missing are skipped automatically.
; ============================================================================

#define AppName       "BuildAI Revit Plugins"
#define AppVersion    "7.4"
#define AppPublisher  "BuildAI"
#define AppURL        "https://app.buildai.me"

[Setup]
AppId={{8F5D2E10-3C77-4B92-AE0D-9B1C2D3E4F50}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}
SetupIconFile=assets\BuildAI_Setup.ico
UninstallDisplayIcon={app}\BuildAI.ico
DefaultDirName={autopf}\BuildAI\RevitPlugins
DisableDirPage=yes
DisableProgramGroupPage=yes
UninstallDisplayName={#AppName}
OutputDir=output
OutputBaseFilename=BuildAI_RevitPlugins_7.4_Setup
; Build the installer without the external Setup Loader.
; This avoids launching a second unsigned EXE from %TEMP%, which can be
; blocked by WDAC/AppLocker/Smart App Control with error 4551.
UseSetupLdr=no
Compression=lzma2/max
SolidCompression=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
Uninstallable=yes
CreateUninstallRegKey=yes
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "he"; MessagesFile: "compiler:Languages\Hebrew.isl"

[Types]
Name: "full";   Description: "All detected Revit versions"
Name: "custom"; Description: "Custom"; Flags: iscustom

[Components]
#if DirExists(AddBackslash(SourcePath) + "payload\2023")
Name: "r2023"; Description: "Revit 2023"; Types: full custom
#endif
#if DirExists(AddBackslash(SourcePath) + "payload\2024")
Name: "r2024"; Description: "Revit 2024"; Types: full custom
#endif
#if DirExists(AddBackslash(SourcePath) + "payload\2025")
Name: "r2025"; Description: "Revit 2025"; Types: full custom
#endif
#if DirExists(AddBackslash(SourcePath) + "payload\2026")
Name: "r2026"; Description: "Revit 2026"; Types: full custom
#endif

[Files]
Source: "assets\BuildAI.ico"; DestDir: "{app}"; Flags: ignoreversion
; Each version's payload -> %ProgramData%\Autodesk\Revit\Addins\<ver>\
#if DirExists(AddBackslash(SourcePath) + "payload\2023")
Source: "payload\2023\*"; DestDir: "{commonappdata}\Autodesk\Revit\Addins\2023"; \
    Flags: ignoreversion recursesubdirs createallsubdirs; Components: r2023
#endif
#if DirExists(AddBackslash(SourcePath) + "payload\2024")
Source: "payload\2024\*"; DestDir: "{commonappdata}\Autodesk\Revit\Addins\2024"; \
    Flags: ignoreversion recursesubdirs createallsubdirs; Components: r2024
#endif
#if DirExists(AddBackslash(SourcePath) + "payload\2025")
Source: "payload\2025\*"; DestDir: "{commonappdata}\Autodesk\Revit\Addins\2025"; \
    Flags: ignoreversion recursesubdirs createallsubdirs; Components: r2025
#endif
#if DirExists(AddBackslash(SourcePath) + "payload\2026")
Source: "payload\2026\*"; DestDir: "{commonappdata}\Autodesk\Revit\Addins\2026"; \
    Flags: ignoreversion recursesubdirs createallsubdirs; Components: r2026
#endif


[Icons]
; Quick access to the standard Inno Setup uninstaller.
Name: "{autoprograms}\BuildAI\Uninstall BuildAI Revit Plugins"; Filename: "{uninstallexe}"
Name: "{commondesktop}\Uninstall BuildAI Revit Plugins"; Filename: "{uninstallexe}"; Tasks: desktopuninstall

[Tasks]
Name: "desktopuninstall"; Description: "Create a desktop shortcut for quick uninstall"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[UninstallDelete]
; Remove the per-version BuildAI subfolders and .addin manifests we created.
Type: filesandordirs; Name: "{commonappdata}\Autodesk\Revit\Addins\2023\BuildAI"
Type: filesandordirs; Name: "{commonappdata}\Autodesk\Revit\Addins\2024\BuildAI"
Type: filesandordirs; Name: "{commonappdata}\Autodesk\Revit\Addins\2025\BuildAI"
Type: filesandordirs; Name: "{commonappdata}\Autodesk\Revit\Addins\2026\BuildAI"
Type: files; Name: "{commonappdata}\Autodesk\Revit\Addins\2023\BuildAI.Plugin1.addin"
Type: files; Name: "{commonappdata}\Autodesk\Revit\Addins\2024\BuildAI.Plugin1.addin"
Type: files; Name: "{commonappdata}\Autodesk\Revit\Addins\2025\BuildAI.Plugin1.addin"
Type: files; Name: "{commonappdata}\Autodesk\Revit\Addins\2026\BuildAI.Plugin1.addin"
Type: files; Name: "{commonappdata}\Autodesk\Revit\Addins\2023\BuildAI.Plugin2.addin"
Type: files; Name: "{commonappdata}\Autodesk\Revit\Addins\2024\BuildAI.Plugin2.addin"
Type: files; Name: "{commonappdata}\Autodesk\Revit\Addins\2025\BuildAI.Plugin2.addin"
Type: files; Name: "{commonappdata}\Autodesk\Revit\Addins\2026\BuildAI.Plugin2.addin"

Type: files; Name: "{commonappdata}\Autodesk\Revit\Addins\2023\BuildAI.Plugin3.addin"
Type: files; Name: "{commonappdata}\Autodesk\Revit\Addins\2024\BuildAI.Plugin3.addin"
Type: files; Name: "{commonappdata}\Autodesk\Revit\Addins\2025\BuildAI.Plugin3.addin"
Type: files; Name: "{commonappdata}\Autodesk\Revit\Addins\2026\BuildAI.Plugin3.addin"

Type: files; Name: "{commonappdata}\Autodesk\Revit\Addins\2023\BuildAI.Plugin4.addin"
Type: files; Name: "{commonappdata}\Autodesk\Revit\Addins\2024\BuildAI.Plugin4.addin"
Type: files; Name: "{commonappdata}\Autodesk\Revit\Addins\2025\BuildAI.Plugin4.addin"
Type: files; Name: "{commonappdata}\Autodesk\Revit\Addins\2026\BuildAI.Plugin4.addin"

Type: files; Name: "{commonappdata}\Autodesk\Revit\Addins\2023\BuildAI.Plugin5.addin"
Type: files; Name: "{commonappdata}\Autodesk\Revit\Addins\2024\BuildAI.Plugin5.addin"
Type: files; Name: "{commonappdata}\Autodesk\Revit\Addins\2025\BuildAI.Plugin5.addin"
Type: files; Name: "{commonappdata}\Autodesk\Revit\Addins\2026\BuildAI.Plugin5.addin"

[Code]
{ Pre-tick the component for each Revit version actually installed on this PC. }
function RevitInstalled(Ver: String): Boolean;
begin
  Result := RegKeyExists(HKLM, 'SOFTWARE\Autodesk\Revit\' + Ver) or
            RegKeyExists(HKLM, 'SOFTWARE\Autodesk\Revit\Autodesk Revit ' + Ver);
end;

procedure InitializeWizard();
begin
  { Nothing required; components default from the selected Type. }
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
end;
