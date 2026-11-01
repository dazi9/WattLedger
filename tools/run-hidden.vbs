' Runs a PowerShell script with no console window and propagates its exit code.
' Used by the "WattLedger auto-deploy" scheduled task so Windows Terminal
' does not pop up every 30 minutes.
' Usage: wscript.exe //B //Nologo run-hidden.vbs [scriptPath]

Dim scriptPath
If WScript.Arguments.Count > 0 Then
    scriptPath = WScript.Arguments(0)
Else
    scriptPath = CreateObject("Scripting.FileSystemObject").GetParentFolderName(WScript.ScriptFullName) & "\update-local.ps1"
End If

Set sh = CreateObject("WScript.Shell")
code = sh.Run("C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe -NoProfile -ExecutionPolicy Bypass -File """ & scriptPath & """", 0, True)
WScript.Quit code
