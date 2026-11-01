<#
    Installs Vortex Backup Tool into D:\Programs\Vortex Backup Tool and creates a desktop shortcut.

    - The .exe to install, in this order: -SourceExe; else a published portable .exe: the one next to this script's
      folder when the script runs from the publish output (bin\Publish\Portable\Scripts\Install.bat, copied there by
      the build), or the "Portable" publish folder of the project (UniversalBackupTool.UI\bin\Publish\Portable);
      else it builds the portable single-file .exe itself (-Build forces that even when a published .exe exists).
      An ordinary build output (an .exe with UniversalBackupTool.UI.dll next to it, e.g. bin\Debug) is never installed:
      it cannot run on its own.
    - The project is found by walking up from this script's folder, so building works from the repository's Scripts
      folder and from the copies in bin\...\Scripts.
    - Only the .exe is copied/replaced. Data next to it (settings.json, profiles.json, Snapshots, Logs, Temp,
      SafetyBackups, Backups) is never touched, so running Install again is a safe update.
    - On a fresh install, if a backup made by Uninstall exists in "G:\Мій диск\Software backups\Vortex Backup Tool",
      it offers to restore that data.

    Run Install.bat (double-click) or: powershell -ExecutionPolicy Bypass -File Install.ps1 [-SourceExe <path>] [-Build]
#>
[CmdletBinding()]
param(
    [string]$InstallRoot = 'D:\Programs',
    [string]$BackupRoot = 'G:\Мій диск\Software backups',
    [string]$ShortcutFolder = [Environment]::GetFolderPath('Desktop'),
    [string]$SourceExe,
    [switch]$Build
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

# The folder holding UniversalBackupTool.UI.csproj: this script's folder or one above it (repository Scripts folder,
# or a copy in bin\<configuration>\...\Scripts or bin\Publish\Portable\Scripts). Null when not found.
function Find-ProjectDirectory {
    $directory = $PSScriptRoot

    while ($directory) {
        if (Test-Path -LiteralPath (Join-Path $directory 'UniversalBackupTool.UI.csproj') -PathType Leaf) { return $directory }
        $directory = Split-Path -Path $directory -Parent
    }

    return $null
}

# A portable single-file .exe carries everything; an ordinary build has UniversalBackupTool.UI.dll next to it.
function Test-PortableExe([string]$path) {
    (Test-Path -LiteralPath $path -PathType Leaf) -and
        -not (Test-Path -LiteralPath (Join-Path (Split-Path -Path $path -Parent) 'UniversalBackupTool.UI.dll') -PathType Leaf)
}

try {
    if (Get-RunningInstance) {
        throw "$AppName is running from $InstallDir. Close it and run Install again."
    }

    # 1. The .exe to install: -SourceExe, else a published portable one, else build it.
    $publishDir = $null
    $projectDir = Find-ProjectDirectory

    # Next to the script's folder first (the script runs from bin\Publish\Portable\Scripts), then the project's
    # Portable publish folder (the script runs from the repository or another build output).
    $candidates = @(
        (Join-Path (Split-Path -Path $PSScriptRoot -Parent) $ExeName),
        (Join-Path $PSScriptRoot $ExeName)
    )

    if ($projectDir) { $candidates += [IO.Path]::Combine($projectDir, 'bin', 'Publish', 'Portable', $ExeName) }

    $publishedExe = $candidates | Where-Object { Test-PortableExe $_ } | Select-Object -First 1

    if ($SourceExe) {
        if (-not (Test-Path -LiteralPath $SourceExe -PathType Leaf)) { throw "Source .exe not found: $SourceExe" }
        $exe = (Resolve-Path -LiteralPath $SourceExe).Path
    }
    elseif (-not $Build -and $publishedExe) {
        $exe = (Resolve-Path -LiteralPath $publishedExe).Path
        $published = (Get-Item -LiteralPath $exe).LastWriteTime
        Write-Host "Using the published .exe: $exe" -ForegroundColor Cyan
        Write-Host "  published $($published.ToString('yyyy-MM-dd HH:mm')). Publish again first if the code changed since, or run with -Build."
    }
    else {
        if (-not $Build) { Write-Host "No published portable .exe found (looked in: $(($candidates | ForEach-Object { [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($_)) } | Select-Object -Unique) -join '; '))." -ForegroundColor Yellow }

        if (-not $projectDir) { throw "UniversalBackupTool.UI.csproj not found above $PSScriptRoot. Publish first, or pass -SourceExe <path to $ExeName>." }

        $project = Join-Path $projectDir 'UniversalBackupTool.UI.csproj'
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
