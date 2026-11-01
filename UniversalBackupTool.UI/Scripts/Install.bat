@echo off
rem Installs Vortex Backup Tool into D:\Programs and creates a desktop shortcut. See Install.ps1.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1" %*
pause
