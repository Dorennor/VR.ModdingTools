@echo off
rem Saves the tool and its data to Google Drive, then removes it from D:\Programs. See Uninstall.ps1.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Uninstall.ps1" %*
pause
