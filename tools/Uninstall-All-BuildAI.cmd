@echo off
setlocal EnableExtensions EnableDelayedExpansion

:: BuildAI Revit Plugins - standalone cleanup utility.
:: Uses Windows built-in commands only. PowerShell is invoked for one thing:
:: requesting elevation. The previous mshta/JScript approach corrupted any path
:: containing a backslash escape sequence.

:: Log to TEMP, not the Desktop. Under elevation %USERPROFILE% resolves to the
:: ELEVATING account, so a Desktop log lands in a profile the user may never see;
:: and on OneDrive-redirected machines %USERPROFILE%\Desktop does not exist at
:: all, in which case every write silently failed while the script still claimed
:: the log had been saved.
set "LOG=%TEMP%\BuildAI_Cleanup.log"
set "SELF=%~f0"

:: Require elevation before anything else. Writing the log first meant the
:: pre-elevation pass created it, then the elevated pass truncated it, so the
:: surviving log described only half the run.
net session >nul 2>&1
if not "%ERRORLEVEL%"=="0" (
  echo Requesting administrator rights...
  :: Relaunch through PowerShell rather than mshta. The previous version built a
  :: JScript string literal containing the script path, and JScript treats
  :: backslashes as escapes: "C:\Users\test" became a TAB character followed by
  :: "est", so elevation failed on most real paths.
  powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -Command "$q=[char]34; try { Start-Process cmd.exe -ArgumentList '/d','/c',($q+$env:SELF+$q) -Verb RunAs -ErrorAction Stop } catch { exit 1 }" >nul 2>&1
  if errorlevel 1 (
    echo.
    echo Could not elevate automatically.
    echo Right-click this file and choose "Run as administrator".
    echo.
    pause
  )
  exit /b
)

echo BuildAI Revit Plugins cleanup started. > "%LOG%"
echo Date: %DATE% %TIME%>> "%LOG%"

echo.
echo ============================================================
echo   BuildAI Revit Plugins - Full Cleanup
echo ============================================================
echo.
echo This will FORCE-CLOSE Revit if it is running. Unsaved work will be lost.
echo All BuildAI add-ins, payload files, caches and settings will be removed.
echo.
set "CONFIRM="
set /p "CONFIRM=Type YES to continue: "
if /I not "!CONFIRM!"=="YES" (
  echo Cancelled. Nothing was changed.
  echo Cancelled by user.>> "%LOG%"
  echo.
  pause
  exit /b 2
)

:: Close Revit so loaded DLLs can be deleted, then give the OS a moment to
:: release the file handles. Deleting immediately after taskkill often fails.
tasklist /FI "IMAGENAME eq Revit.exe" 2>nul | find /I "Revit.exe" >nul
if not errorlevel 1 (
  echo Closing Revit...
  taskkill /F /IM Revit.exe /T >> "%LOG%" 2>&1
  ping -n 4 127.0.0.1 >nul
)

:: Uninstall all MSI products whose DisplayName contains BuildAI and Revit.
call :UninstallRegistryRoot "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"
call :UninstallRegistryRoot "HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
call :UninstallRegistryRoot "HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"

:: Run any old Inno Setup uninstallers silently.
call :RunOldUninstallers "%ProgramFiles%\BuildAI"
if defined ProgramFiles(x86) call :RunOldUninstallers "%ProgramFiles(x86)%\BuildAI"
call :RunOldUninstallers "%ProgramData%\BuildAI"

:: Remove Revit add-in payloads from all likely versions and locations.
for %%R in (2020 2021 2022 2023 2024 2025 2026 2027) do (
  call :CleanAddinFolder "%ProgramData%\Autodesk\Revit\Addins\%%R"
  call :CleanAddinFolder "%AppData%\Autodesk\Revit\Addins\%%R"
)

:: Remove known installation and test-data directories.
call :RemoveTree "%ProgramFiles%\BuildAI"
if defined ProgramFiles(x86) call :RemoveTree "%ProgramFiles(x86)%\BuildAI"
call :RemoveTree "%ProgramData%\BuildAI"
call :RemoveTree "%LocalAppData%\BuildAI"
call :RemoveTree "%AppData%\BuildAI"

:: Remove common legacy folders used by earlier test packages.
call :RemoveTree "%ProgramData%\Autodesk\ApplicationPlugins\BuildAI.bundle"
call :RemoveTree "%AppData%\Autodesk\ApplicationPlugins\BuildAI.bundle"

:: Remove stale shortcuts.
del /F /Q "%Public%\Desktop\BuildAI*.lnk" >> "%LOG%" 2>&1
del /F /Q "%UserProfile%\Desktop\BuildAI*.lnk" >> "%LOG%" 2>&1

:: Verify. A cleanup that reports success while leaving a manifest behind is
:: worse than one that fails loudly: Revit still tries to load the plugin.
set "LEFTOVERS=0"
for %%R in (2020 2021 2022 2023 2024 2025 2026 2027) do (
  call :CountLeftovers "%ProgramData%\Autodesk\Revit\Addins\%%R"
  call :CountLeftovers "%AppData%\Autodesk\Revit\Addins\%%R"
)

echo.
if "!LEFTOVERS!"=="0" (
  echo Cleanup completed. No BuildAI files remain.
  echo Cleanup completed successfully.>> "%LOG%"
) else (
  echo WARNING: cleanup finished but !LEFTOVERS! BuildAI file^(s^) could not be removed.
  echo Close Revit and any Explorer window showing the add-in folder, then run this tool again.
  echo Cleanup finished with !LEFTOVERS! leftover file^(s^).>> "%LOG%"
)
echo Log saved to: %LOG%
echo.
echo You can now install the next BuildAI test build.
echo.
pause
if not "!LEFTOVERS!"=="0" exit /b 1
exit /b 0

:UninstallRegistryRoot
set "ROOT=%~1"
for /f "usebackq delims=" %%K in (`reg query "%ROOT%" 2^>nul`) do (
  set "NAME="
  for /f "tokens=2,*" %%A in ('reg query "%%K" /v DisplayName 2^>nul ^| find /I "DisplayName"') do set "NAME=%%B"
  if defined NAME (
    echo !NAME! | find /I "BuildAI" >nul
    if not errorlevel 1 (
      echo !NAME! | find /I "Revit" >nul
      if not errorlevel 1 call :UninstallRegistryKey "%%K" "!NAME!"
    )
  )
)
exit /b

:UninstallRegistryKey
set "KEY=%~1"
set "PRODUCT=%~2"
set "GUID="
for %%G in ("%KEY%") do set "GUID=%%~nxG"

echo Removing MSI product: %PRODUCT%
echo MSI key: %KEY%>> "%LOG%"

echo %GUID% | findstr /R /X "{[0-9A-Fa-f-][0-9A-Fa-f-]*}" >nul
if not errorlevel 1 (
  start /wait "" msiexec.exe /x "%GUID%" /qn /norestart /L*v "%TEMP%\BuildAI-MSI-Uninstall.log"
  exit /b
)

:: Fallback for non-GUID uninstall keys.
set "UNINSTALL="
for /f "tokens=2,*" %%A in ('reg query "%KEY%" /v QuietUninstallString 2^>nul ^| find /I "QuietUninstallString"') do set "UNINSTALL=%%B"
if not defined UNINSTALL for /f "tokens=2,*" %%A in ('reg query "%KEY%" /v UninstallString 2^>nul ^| find /I "UninstallString"') do set "UNINSTALL=%%B"
if defined UNINSTALL start /wait "" cmd.exe /d /c "!UNINSTALL! /quiet /norestart"
exit /b

:RunOldUninstallers
set "OLDROOT=%~1"
if not exist "%OLDROOT%" exit /b
for /r "%OLDROOT%" %%U in (unins*.exe) do (
  echo Running legacy uninstaller: %%~fU
  start /wait "" "%%~fU" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
)
exit /b

:CleanAddinFolder
set "ADDINROOT=%~1"
if not exist "%ADDINROOT%" exit /b

echo Cleaning %ADDINROOT%>> "%LOG%"

:: Current layouts.
call :RemoveTree "%ADDINROOT%\BuildAI"
call :RemoveTree "%ADDINROOT%\BuildAI.RevitSuite"
call :RemoveTree "%ADDINROOT%\BuildAI Revit Plugins"

:: Manifests and binaries placed directly in the Revit version folder.
del /F /Q "%ADDINROOT%\BuildAI*.addin" >> "%LOG%" 2>&1
del /F /Q "%ADDINROOT%\BuildAI*.dll" >> "%LOG%" 2>&1
del /F /Q "%ADDINROOT%\BuildAI*.pdb" >> "%LOG%" 2>&1
del /F /Q "%ADDINROOT%\BuildAI*.json" >> "%LOG%" 2>&1

:: Third-party payload shipped alongside the plugins. None of these start with
:: "BuildAI", so the mask above never matched them and every cleanup left them
:: behind. A stale Newtonsoft.Json.dll in the add-in folder can then bind into
:: the NEXT build, which defeats the whole point of a clean-slate tool.
del /F /Q "%ADDINROOT%\Newtonsoft.Json.dll" >> "%LOG%" 2>&1
del /F /Q "%ADDINROOT%\Microsoft.Web.WebView2.Core.dll" >> "%LOG%" 2>&1
del /F /Q "%ADDINROOT%\Microsoft.Web.WebView2.Wpf.dll" >> "%LOG%" 2>&1
del /F /Q "%ADDINROOT%\WebView2Loader.dll" >> "%LOG%" 2>&1

:: Packaged UI resources and satellite localisation folders.
call :RemoveTree "%ADDINROOT%\ViewerProbe"
call :RemoveTree "%ADDINROOT%\he"

:: Clean subfolders whose names begin with BuildAI.
for /d %%D in ("%ADDINROOT%\BuildAI*") do call :RemoveTree "%%~fD"
exit /b

:CountLeftovers
set "CHECKROOT=%~1"
if not exist "%CHECKROOT%" exit /b
for %%F in ("%CHECKROOT%\BuildAI*.addin" "%CHECKROOT%\BuildAI*.dll") do (
  if exist "%%~F" (
    set /a LEFTOVERS+=1
    echo LEFTOVER: %%~fF>> "%LOG%"
  )
)
exit /b

:RemoveTree
set "TARGET=%~1"
if not defined TARGET exit /b
if exist "%TARGET%" (
  echo Removing directory: %TARGET%
  attrib -R -S -H "%TARGET%" /S /D >> "%LOG%" 2>&1
  rmdir /S /Q "%TARGET%" >> "%LOG%" 2>&1
  if exist "%TARGET%" (
    echo FAILED to remove: %TARGET%>> "%LOG%"
    echo   ^(still present - a file may be locked^)
  )
)
exit /b
