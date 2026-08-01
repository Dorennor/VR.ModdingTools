@echo off
setlocal enabledelayedexpansion

REM Check if VortexBackupTool.exe exists in current directory
if not exist "VortexBackupTool.exe" (
	echo Error: VortexBackupTool.exe not found in current directory
	pause
	exit /b 1
)

REM Get current directory
set SCRIPT_DIR=%cd%
set EXE_PATH=!SCRIPT_DIR!\VortexBackupTool.exe
set DESKTOP_PATH=%USERPROFILE%\Desktop
set SHORTCUT_PATH=!DESKTOP_PATH!\VortexBackupTool.lnk

REM Create VBScript in temp file
set VBSCRIPT=%TEMP%\CreateShortcut_temp.vbs
(
echo Set oWS = WScript.CreateObject("WScript.Shell"^)
echo Set oLink = oWS.CreateShortcut("!SHORTCUT_PATH!"^)
echo oLink.TargetPath = "!EXE_PATH!"
echo oLink.WorkingDirectory = "!SCRIPT_DIR!"
echo oLink.Description = "Vortex Backup Tool"
echo oLink.Save
) > "!VBSCRIPT!"

REM Run VBScript
cscript.exe "!VBSCRIPT!" //nologo

REM Clean up temp file
del "!VBSCRIPT!" /q 2>nul

echo.
if exist "!SHORTCUT_PATH!" (
	echo Success! Desktop shortcut created
	echo Location: !DESKTOP_PATH!\VortexBackupTool.lnk
) else (
	echo Error: Failed to create shortcut
)
echo.
pause
endlocal

