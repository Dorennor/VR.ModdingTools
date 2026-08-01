@echo off
REM Simple desktop shortcut creator for VortexBackupTool

if not exist "VortexBackupTool.exe" (
	echo Error: VortexBackupTool.exe not found
	pause
	exit /b 1
)

REM Create VBScript content directly
(
echo Set shell = CreateObject("WScript.Shell"^)
echo Set fso = CreateObject("Scripting.FileSystemObject"^)
echo exePath = fso.GetAbsolutePathName("VortexBackupTool.exe"^)
echo deskPath = shell.SpecialFolders("Desktop"^)
echo lnkPath = deskPath ^& "\VortexBackupTool.lnk"
echo Set link = shell.CreateShortcut(lnkPath^)
echo link.TargetPath = exePath
echo link.WorkingDirectory = fso.GetParentFolderName(exePath^)
echo link.Description = "Vortex Backup Tool"
echo link.Save
echo WScript.Echo "Done"
) > "%temp%\shortcut.vbs"

cscript.exe "%temp%\shortcut.vbs" //nologo
del "%temp%\shortcut.vbs" /q 2>nul

echo Shortcut created on Desktop
pause


