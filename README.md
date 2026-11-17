# VR.ModdingTools

**Vortex Backup Tool** is a portable Windows app for backing up modded games. It is built around [Vortex](https://www.nexusmods.com/about/vortex/) mod setups but also works for games without Vortex and for any extra folders or files you choose.

Backups are incremental: each mod is stored in its own archive, and a new backup only writes the mods that changed. A one-mod update to a 100 GB modpack takes seconds, not hours.

> The tool is in active development (version 0.x). Back up anything irreplaceable some other way as well until it reaches 1.0.

## Features

### Profiles

A profile is one game: its game folder, its Vortex staging folder, and any custom paths (folders or single files). Profiles can be marked as not managed by Vortex.

- Each path has its own drive switch, and **Find on other disk** looks for a moved folder on your other drives.
- A compact view shows each profile as a single row.

### Vanilla & Purge

- **Vanilla snapshot.** Record the clean game folder: every file with its size, times, version and SHA-256 hash, plus every folder.
- **Purge.** Return the game folder to its snapshot state. Files that aren't in the snapshot are deleted, files a mod moved are put back, and folders left empty are removed. A preview shows everything first, and a safety backup is made before anything is deleted.
- **Links are safe.** Hardlinks, symlinks and junctions that Vortex deployed are removed as links only. Their targets are never touched.
- **Purge tracked files.** After a game update, remove the files your last backup recorded, but only those whose content is still identical to that backup.
- **Add missing details.** Upgrade an older snapshot with hashes, versions and creation times without recreating it.

### Backup

Pick one or more games and the parts to back up. Every part is off until you turn it on.

| Component | What it contains |
|---|---|
| Staging | The Vortex staging folder, one archive per mod. |
| Vortex AppData | `%APPDATA%\Vortex` (mod lists, profiles, load orders and settings of every game), backed up whole, once per run. |
| Game configs | New files in the game folder that aren't in the vanilla snapshot, such as generated `.ini` and `.json` files. |
| New game files | Game configs plus the mod files deployed into the game folder. Judged by metadata only, so it is fast. |
| Changed game files | Everything that differs from vanilla, links stored as real files. One archive you can unpack into another copy of the game. |
| Custom paths | The extra folders and files of the profile. |
| Full game | The whole game folder as one plain zip in a folder you choose, ready to unpack and play. |

- **Only changes are written.** Unchanged files are recognised by their metadata and stored hashes, and a file whose content didn't change is never archived again.
- **Locked files don't stop the run.** Files that are locked while archiving are recorded, and **Continue Backup** finishes them later, even after the app restarts.
- **Faster archiving.** Archives are written in parallel, and file types that don't compress well (`.ba2`, audio, video, other archives) are stored without compression.
- **Old backups are cleaned up.** For each game and component, the newest N complete backups are kept, and archives they no longer use are deleted.
- **Vortex files are skipped in the game folder.** An ignore list keeps Vortex's own files (deployment manifests, folder markers) out of the game folder components.

### Restore

Choose a game, then one of its backups, then the components and target drives. Restore makes a safety backup first and asks before it overwrites anything. Backups made by older versions of the tool can still be restored.

### Everything else

- An Output panel and status bar show progress, warnings and errors for every operation. An operation keeps running when you switch pages and can be cancelled at any time.
- Daily log files, kept for 30 days, also record how long each file took to archive.
- Light, dark and system themes. The layout adapts to the window size.

## Requirements

- Windows 10 or 11, 64-bit.
- To build it yourself: the .NET 10 SDK.

The published .exe is self-contained and doesn't need .NET installed.

## Install

### From a release

1. Download `UniversalBackupTool.UI.exe` from the [latest release](https://github.com/Dorennor/VR.ModdingTools/releases/latest).
2. Put it in a folder of its own, for example `D:\Programs\Vortex Backup Tool`, and run it.

The app is portable. It keeps all its data next to the .exe:

| Item | Contents |
|---|---|
| `settings.json`, `profiles.json` | Settings and game profiles |
| `Snapshots` | Vanilla snapshots |
| `Logs` | Daily log files |
| `SafetyBackups` | Copies made before a purge or restore |
| `Temp` | The paused backup job, if any |

Copy the whole folder to move the app to another PC or drive. Backup destinations inside the folder are stored as relative paths, so they keep working.

The .exe isn't code-signed, so Windows SmartScreen may warn you the first time you run it.

### With the install scripts

`UniversalBackupTool.UI/Scripts` holds `Install.bat` and `Uninstall.bat` (wrappers for the `.ps1` scripts):

- **Install** copies the portable .exe to `<InstallRoot>\Vortex Backup Tool` and creates a desktop shortcut. If there is no published .exe yet, it builds one. It only replaces the .exe, so running it again is a safe update.
- **Uninstall** first mirrors the whole program folder, data included, to `<BackupRoot>\Vortex Backup Tool`, checks the copy, and only then removes the program and the shortcut. If the copy fails, nothing is removed.

The default paths are the author's (`D:\Programs`, and a Google Drive folder for `BackupRoot`). Pass your own:

```powershell
powershell -ExecutionPolicy Bypass -File UniversalBackupTool.UI\Scripts\Install.ps1 -InstallRoot "C:\Tools"
powershell -ExecutionPolicy Bypass -File UniversalBackupTool.UI\Scripts\Uninstall.ps1 -InstallRoot "C:\Tools" -BackupRoot "E:\Backups"
```

## Where backups are stored

```text
<backup destination>\
├── <Game>\
│   ├── staging\
│   │   ├── _archives\            shared archives, one per mod
│   │   ├── 2026-11-17_12-00-00\  one backup: its manifest references archives in _archives
│   │   └── ...
│   ├── configs\
│   ├── additions\
│   ├── changes\
│   └── custom\
└── #Vortex AppData#\
```

Each backup is a dated folder with a manifest that points to archives in the shared `_archives` folder. When you move or copy backups, always keep `_archives` together with the dated folders.

Full game archives are not stored here. They go to the export folder set in the profile, as `<Game>.zip`. The old archive is deleted before the new one is written, never overwritten, so a cloud drive client doesn't download it first.

## Build from source

```powershell
git clone https://github.com/Dorennor/VR.ModdingTools.git
cd VR.ModdingTools
dotnet build
dotnet test
```

To build the portable single-file .exe, run the following. The output goes to `UniversalBackupTool.UI\bin\Publish\Portable`.

```powershell
dotnet publish UniversalBackupTool.UI/UniversalBackupTool.UI.csproj -p:PublishProfile=Portable
```

In Visual Studio, right-click `UniversalBackupTool.UI` and choose **Publish > Portable**.

### Solution layout

| Project | Target | Contents |
|---|---|---|
| `UniversalBackupTool.Models` | `net10.0` | Data types: profiles, settings, manifests, snapshots |
| `UniversalBackupTool.Interfaces` | `net10.0` | Service contracts |
| `UniversalBackupTool.Services` | `net10.0` | Backup, restore, snapshots, purge, file system and storage |
| `UniversalBackupTool.ViewModels` | `net10.0` | MVVM view models (CommunityToolkit.Mvvm) |
| `UniversalBackupTool.UI` | `net10.0-windows` | WPF app (WPF-UI), dependency injection setup, install scripts |
| `UniversalBackupTool.Tests` | `net10.0` | NUnit tests for services and view models |

Some tests create real hardlinks, junctions and symlinks. The symlink tests skip themselves when Windows doesn't allow creating symlinks; to run them, turn on Developer Mode or run as administrator.

### Versioning

The version is set in one place: `<Version>` in `Directory.Build.props`, in the form `0.MINOR.PATCH`.

- **Patch.** A pre-commit hook raises the patch number on every commit. Enable it once per clone with `git config core.hooksPath .githooks`.
- **Minor.** Set it by hand for a new feature or a major rework.

The rules and the full history are in [Documentation/Version_History.md](Documentation/Version_History.md).

## Documentation

- [Project context](Documentation/Vortex_Backup_Tool_Project_Context.md): the detailed design, the decisions behind it and the current state.
- [Version history](Documentation/Version_History.md).
- `Documentation/Vortex_Backup_Tool_Rationale.txt` (in Ukrainian) and `Vortex_Backup_Tool_Original_Plan.docx`: the original problem description and plan.
