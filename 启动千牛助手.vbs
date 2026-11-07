Option Explicit
Dim shell, fs, root, exe
Set shell = CreateObject("WScript.Shell")
Set fs = CreateObject("Scripting.FileSystemObject")
root = fs.GetParentFolderName(WScript.ScriptFullName)
exe = root & "\DesktopClient\bin\Release\net8.0-windows\win-x64\QNAgent.exe"
If Not fs.FileExists(exe) Then
  MsgBox "QN Agent executable not found. Please run Start-Desktop.ps1.", 16, "QN Agent"
  WScript.Quit 1
End If
shell.CurrentDirectory = root
shell.Environment("PROCESS")("DOTNET_ROOT") = root & "\.tools\dotnet"
shell.Environment("PROCESS")("DOTNET_ROOT_X64") = root & "\.tools\dotnet"
On Error Resume Next
shell.Run Chr(34) & exe & Chr(34), 1, False
If Err.Number <> 0 Then
  MsgBox "Unable to start QN Agent: " & Err.Description, 16, "QN Agent"
  WScript.Quit 1
End If
