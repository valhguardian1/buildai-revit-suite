Option Explicit
Dim shell, fso, baseDir, sourceCmd, tempDir, targetCmd, args, answer
Set shell = CreateObject("Shell.Application")
Set fso = CreateObject("Scripting.FileSystemObject")
baseDir = fso.GetParentFolderName(WScript.ScriptFullName)
sourceCmd = fso.BuildPath(baseDir, "Uninstall-All-BuildAI.cmd")
tempDir = fso.GetSpecialFolder(2)
targetCmd = fso.BuildPath(tempDir, "BuildAI-Full-Cleanup.cmd")

If Not fso.FileExists(sourceCmd) Then
  MsgBox "BuildAI Cleaner is incomplete: Uninstall-All-BuildAI.cmd was not extracted." & vbCrLf & _
         "Download the cleanup tool again.", 16, "BuildAI Cleaner"
  WScript.Quit 1
End If

On Error Resume Next
fso.CopyFile sourceCmd, targetCmd, True
If Err.Number <> 0 Then
  MsgBox "BuildAI Cleaner could not prepare the cleanup script." & vbCrLf & Err.Description, 16, "BuildAI Cleaner"
  WScript.Quit 1
End If
On Error GoTo 0

' State the destructive part up front and let the user back out. The script
' force-closes Revit, so an information-only prompt was not enough.
answer = MsgBox("BuildAI Cleaner will remove all BuildAI add-ins, payload files, caches and settings." & vbCrLf & vbCrLf & _
                "Revit will be FORCE-CLOSED if it is running. Save your work first." & vbCrLf & vbCrLf & _
                "Administrator permission will be requested, and a console window will stay open " & _
                "showing the result." & vbCrLf & vbCrLf & _
                "Continue?", 49, "BuildAI Cleaner")
If answer <> 1 Then WScript.Quit 2

args = "/d /c """ & targetCmd & """"

' ShellExecute raises a runtime error when the user dismisses the UAC prompt.
' Unhandled, that surfaced as a raw Windows Script Host error dialog.
On Error Resume Next
shell.ShellExecute "cmd.exe", args, tempDir, "runas", 1
If Err.Number <> 0 Then
  MsgBox "Administrator permission was not granted, so nothing was changed." & vbCrLf & vbCrLf & _
         "To run the cleaner manually, right-click this file and choose " & _
         """Run as administrator"":" & vbCrLf & targetCmd, 48, "BuildAI Cleaner"
  WScript.Quit 3
End If
On Error GoTo 0
