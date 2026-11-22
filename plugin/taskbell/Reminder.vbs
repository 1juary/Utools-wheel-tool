Option Explicit
Dim mode, shell, files, scriptPath, powershell, command
If WScript.Arguments.Count <> 1 Then WScript.Quit 1
mode = WScript.Arguments(0)
If mode <> "Alarm" And mode <> "Recover" Then WScript.Quit 1
Set shell = CreateObject("WScript.Shell")
Set files = CreateObject("Scripting.FileSystemObject")
scriptPath = files.BuildPath(files.GetParentFolderName(WScript.ScriptFullName), "Bell.ps1")
powershell = shell.ExpandEnvironmentStrings("%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe")
command = Chr(34) & powershell & Chr(34) & " -NoProfile -STA -ExecutionPolicy Bypass -File " & Chr(34) & scriptPath & Chr(34) & " " & mode
WScript.Quit shell.Run(command, 0, True)
