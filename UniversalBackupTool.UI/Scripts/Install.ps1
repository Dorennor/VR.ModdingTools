<#
    Installs Vortex Backup Tool into D:\Programs\Vortex Backup Tool and creates a desktop shortcut.

    - Builds the portable single-file .exe (publish profile "Portable") from this repository, or uses -SourceExe.
    - Only the .exe is copied/replaced. Data next to it (settings.json, profiles.json, Snapshots, Logs, Temp,
      SafetyBackups, Backups) is never touched, so running Install again is a safe update.
    - On a fresh install, if a backup made by Uninstall exists in "G:\Мій диск\Software backups\Vortex Backup Tool",
      it offers to restore that data.

    Run Install.bat (double-click) or: powershell -ExecutionPolicy Bypass -File Install.ps1 [-SourceExe <path>]
#>
[CmdletBinding()]
param(
    [string]$InstallRoot = 'D:\Programs',
    [string]$BackupRoot = 'G:\Мій диск\Software backups',
    [string]$ShortcutFolder = [Environment]::GetFolderPath('Desktop'),
    [string]$SourceExe
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

try {
    if (Get-RunningInstance) {
        throw "$AppName is running from $InstallDir. Close it and run Install again."
    }

    # 1. The .exe to install.
    $publishDir = $null

    if ($SourceExe) {
        if (-not (Test-Path -LiteralPath $SourceExe -PathType Leaf)) { throw "Source .exe not found: $SourceExe" }
        $exe = (Resolve-Path -LiteralPath $SourceExe).Path
    }
    else {
        $project = Join-Path $PSScriptRoot '..\UniversalBackupTool.UI.csproj'

        if (-not (Test-Path -LiteralPath $project)) { throw "Project not found: $project. Pass -SourceExe <path to $ExeName>." }
        if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'The .NET SDK (dotnet) is not installed. Pass -SourceExe <path to a published exe>.' }

        $publishDir = Join-Path ([IO.Path]::GetTempPath()) ("VortexBackupTool_publish_" + [Guid]::NewGuid().ToString('N'))
        Write-Host "Building the portable .exe (this takes a minute)..." -ForegroundColor Cyan
        & dotnet publish $project -p:PublishProfile=Portable -o $publishDir --nologo -v:minimal
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit code $LASTEXITCODE)." }

        $exe = Join-Path $publishDir $ExeName
        if (-not (Test-Path -LiteralPath $exe)) { throw "Publish output does not contain $ExeName." }
    }

    # 2. Copy the .exe; existing data next to it stays as it is.
    $isFreshInstall = -not (Test-Path -LiteralPath ([IO.Path]::Combine($InstallDir, 'settings.json'))) -and
                      -not (Test-Path -LiteralPath ([IO.Path]::Combine($InstallDir, 'profiles.json')))

    New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
    Copy-Item -LiteralPath $exe -Destination (Join-Path $InstallDir $ExeName) -Force
    Write-Host "Installed $ExeName to $InstallDir" -ForegroundColor Green

    if ($publishDir) { Remove-Item -LiteralPath $publishDir -Recurse -Force -ErrorAction SilentlyContinue }

    # 3. Fresh install: offer the data saved by Uninstall.
    $hasBackup = (Test-Path -LiteralPath ([IO.Path]::Combine($BackupDir, 'settings.json'))) -or
                 (Test-Path -LiteralPath ([IO.Path]::Combine($BackupDir, 'profiles.json')))

    if ($isFreshInstall -and $hasBackup) {
        Write-Host ''
        Write-Host "A saved copy of the tool's data was found in:" -ForegroundColor Yellow
        Write-Host "  $BackupDir"
        $answer = Read-Host 'Restore settings, profiles, snapshots and other data from it? [Y/N]'

        if ($answer -match '^(y|yes|т|так)$') {
            # The installed (newer) .exe is kept; everything else is copied.
            & robocopy $BackupDir $InstallDir /E /XF $ExeName /R:2 /W:2 /NP /NFL /NDL /NJH /NJS | Out-Null
            if ($LASTEXITCODE -ge 8) { throw "Copying the saved data failed (robocopy exit code $LASTEXITCODE)." }
            Write-Host 'Saved data restored.' -ForegroundColor Green
        }
    }

    # 4. Desktop shortcut.
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($ShortcutPath)
    $shortcut.TargetPath = Join-Path $InstallDir $ExeName
    $shortcut.WorkingDirectory = $InstallDir
    $shortcut.IconLocation = (Join-Path $InstallDir $ExeName) + ',0'
    $shortcut.Description = $AppName
    $shortcut.Save()
    Write-Host "Desktop shortcut created: $ShortcutPath" -ForegroundColor Green

    Write-Host ''
    Write-Host "$AppName is installed." -ForegroundColor Green
    exit 0
}
catch {
    Write-Host ''
    Write-Host "Install failed: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
