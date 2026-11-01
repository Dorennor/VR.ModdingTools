<#
    Uninstalls Vortex Backup Tool from D:\Programs\Vortex Backup Tool and removes the desktop shortcut.

    Before anything is removed, the WHOLE program folder is saved to
    "G:\Мій диск\Software backups\Vortex Backup Tool": the .exe, settings.json, profiles.json, Snapshots (vanilla
    baselines with hashes), Temp (paused backup job), Logs, SafetyBackups and Backups (if the backup destination is
    inside the program folder). Paths inside the program folder are stored relative to it, so this folder can be
    copied to any folder on another PC and the .exe run from there: it works as before.

    The saved copy mirrors the current state (files no longer present are removed from it), so it never contains
    stale snapshots. If the backup drive is not available or the copy fails, NOTHING is removed.

    Run Uninstall.bat (double-click) or: powershell -ExecutionPolicy Bypass -File Uninstall.ps1 [-Force]
#>
[CmdletBinding()]
param(
    [string]$InstallRoot = 'D:\Programs',
    [string]$BackupRoot = 'G:\Мій диск\Software backups',
    [string]$ShortcutFolder = [Environment]::GetFolderPath('Desktop'),
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$AppName = 'Vortex Backup Tool'
$ExeName = 'UniversalBackupTool.UI.exe'
$InstallDir = [IO.Path]::Combine($InstallRoot, $AppName)
$BackupDir = [IO.Path]::Combine($BackupRoot, $AppName)
$ShortcutPath = [IO.Path]::Combine($ShortcutFolder, "$AppName.lnk")

function Get-RunningInstance {
    Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($ExeName)) -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($InstallDir, [StringComparison]::OrdinalIgnoreCase) }
}

function Get-FolderSummary([string]$path) {
    $files = @(Get-ChildItem -LiteralPath $path -Recurse -File -Force -ErrorAction Stop)
    [pscustomobject]@{ Count = $files.Count; Bytes = ($files | Measure-Object -Property Length -Sum).Sum }
}

try {
    if (-not (Test-Path -LiteralPath $InstallDir)) {
        if (Test-Path -LiteralPath $ShortcutPath) {
            Remove-Item -LiteralPath $ShortcutPath -Force
            Write-Host "Removed the leftover desktop shortcut." -ForegroundColor Green
        }

        Write-Host "$AppName is not installed in $InstallDir. Nothing else to do."
        exit 0
    }

    if (Get-RunningInstance) {
        throw "$AppName is running. Close it (and let any backup finish) and run Uninstall again."
    }

    $backupDrive = Split-Path -Path $BackupRoot -Qualifier
    if (-not (Test-Path -LiteralPath ($backupDrive + '\'))) {
        throw "Drive $backupDrive is not available (is Google Drive running?). Nothing was removed."
    }

    if (-not $Force) {
        Write-Host "This will:" -ForegroundColor Yellow
        Write-Host "  1. save the whole program folder to: $BackupDir"
        Write-Host "  2. then delete: $InstallDir"
        Write-Host "  3. and delete the desktop shortcut."
        $answer = Read-Host 'Continue? [Y/N]'
        if ($answer -notmatch '^(y|yes|т|так)$') { Write-Host 'Cancelled. Nothing was changed.'; exit 0 }
    }

    # 1. Save everything. /MIR makes the saved copy equal to the program folder.
    New-Item -ItemType Directory -Force -Path $BackupDir | Out-Null
    Write-Host "Saving $InstallDir to $BackupDir ..." -ForegroundColor Cyan
    & robocopy $InstallDir $BackupDir /MIR /R:2 /W:2 /NP /NFL /NDL /NJH /NJS | Out-Null
    $code = $LASTEXITCODE
    if ($code -ge 8) { throw "Saving the program folder failed (robocopy exit code $code). Nothing was removed." }

    # 2. Verify the copy before deleting anything.
    $source = Get-FolderSummary $InstallDir
    $saved = Get-FolderSummary $BackupDir
    if ($source.Count -ne $saved.Count -or $source.Bytes -ne $saved.Bytes) {
        throw "The saved copy does not match the program folder ($($saved.Count) of $($source.Count) files). Nothing was removed."
    }
    Write-Host ("Saved {0:N0} file(s), {1:N1} MB." -f $saved.Count, ($saved.Bytes / 1MB)) -ForegroundColor Green

    # 3. Remove the shortcut and the program folder.
    if (Test-Path -LiteralPath $ShortcutPath) {
        Remove-Item -LiteralPath $ShortcutPath -Force
        Write-Host "Removed the desktop shortcut." -ForegroundColor Green
    }

    Remove-Item -LiteralPath $InstallDir -Recurse -Force
    Write-Host "Removed $InstallDir" -ForegroundColor Green

    Write-Host ''
    Write-Host "$AppName is uninstalled. Its data is saved in:" -ForegroundColor Green
    Write-Host "  $BackupDir"
    Write-Host 'To use it on another PC: copy that folder anywhere and run the .exe in it (or run Install, which offers to restore it).'
    exit 0
}
catch {
    Write-Host ''
    Write-Host "Uninstall failed: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
