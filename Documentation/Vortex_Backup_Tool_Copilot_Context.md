# Vortex Backup Tool - Detailed Copilot Implementation Context

## 1. Architecture & App Context

- **Platform:** .NET 8 Desktop Application.
- **UI Framework:** WPF with `Wpf UI` (lepo.co) for Windows 11 native controls (Mica, NavigationView, Cards).
- **Architecture:** MVVM using `CommunityToolkit.Mvvm` (Source Generators: `[ObservableProperty]`, `[RelayCommand]`).
- **Dependency Injection:** `Microsoft.Extensions.DependencyInjection` for resolving ViewModels and Services.
- **Deployment Strategy:** Single-file portable `.exe`. All app data must be stored locally next to the executable (Self-contained pattern). Do NOT use `%AppData%` for internal app configurations.

## 2. File System Structure (Self-Contained)

The app resides in a single folder and generates the following structure dynamically:

```text
VortexBackupTool/
├── VortexBackupTool.exe
├── settings.json         (Global settings, paths, dynamic compression mapping rules)
├── profiles.json         (Game specific profiles, sources, custom paths)
├── Logs/                 (Application execution logs, text files)
├── Temp/                 (Working directory, contains active_backup_job.json for resumability)
├── SafetyBackups/        (Pre-purge / Pre-restore automatic fast backups)
└── Snapshots/            (Baseline JSON snapshots for game root folders)
```

## 3. Core Data Models & Settings (C# Records)

### 3.1 Global Settings

```csharp
public record AppSettings(
    string GoogleDriveBackupPath,
    int SafetyBackupRetentionDays = 14, // Default retention for auto-backups
    List<CompressionRule> CompressionRules
);

// Dynamic Compression Mapping Rules
public record CompressionRule(
    long MaxSizeBytes, // e.g., 50 * 1024 * 1024 (50 MB)
    System.IO.Compression.CompressionLevel Level // Optimal, Fastest, or NoCompression
);
```

### 3.2 Profile Definition

```csharp
public record BackupProfile(
    Guid Id,
    string GameName,
    string? VortexStagingPath, // Used for standard Vortex mods (e.g., D:\Vortex Mods\skyrimse)
    string? GameRootPath,      // Used for Vanilla Snapshots & custom config extraction (e.g., C:\Games\Skyrim)
    string? VortexAppDataPath, // %AppData%\Vortex specific to this game
    List<string> CustomFolders, // e.g., Documents\Elder Scrolls Online\Addons
    bool UseIgnoreList
);
```

### 3.3 Snapshot Data Structures

```csharp
public record FileMetadata(string RelativePath, long SizeBytes, DateTime LastWriteTime, bool IsSymlink);
public record SnapshotData(string ProfileId, Dictionary<string, FileMetadata> Files); // Dictionary ensures O(1) diff lookups
```

## 4. Key Services & Logic Requirements

### 4.1 IFileSystemProvider

- Must properly handle `FileAttributes.ReparsePoint` (Symlinks) via `.NET 6+ File.ResolveLinkTarget`.
- When backing up a Symlink, the engine must resolve the target and archive the _actual file bytes_, not the shortcut.

### 4.2 ISnapshotService

- `CreateSnapshotAsync`: Reads directory, builds `Dictionary<string, FileMetadata>`.
- `GetDiffAsync`: Compares current state vs Baseline Snapshot.
- `PurgeUnregisteredFilesAsync`: Deletes anything in the Root folder not present in the baseline snapshot.
- **CRITICAL REQUIREMENT:** Must call `ISafetyBackupService.CreateSafetyBackupAsync` BEFORE performing destructive deletion.

### 4.3 IBackupEngine & Resumability (File in Use)

- Uses `System.IO.Compression.ZipArchive`.
- **Dynamic Compression:** For each file, evaluates `file.Length` against `AppSettings.CompressionRules` to set `CompressionLevel` (e.g., files > 500MB get `NoCompression` for speed).
- **Resumability Logic:**
  - If a file throws `IOException` (locked by AV or game), log the failed path to `Temp/active_backup_job.json`.
  - Continue zipping the rest of the files. Do NOT crash.
  - On the next application start (or retry button click), read `active_backup_job.json` and open the existing `.zip` using `ZipArchiveMode.Update` to append ONLY the missing files.

### 4.3.1 Incremental Mod Snapshots

- Each backup run creates a separate timestamped snapshot manifest; older snapshots remain restorable.
- Staging is split into one archive unit per top-level mod folder. Archives live once in the game's shared `_archives/` directory, and snapshot manifests reference them. Keep `_archives/` alongside the dated snapshot directories when moving/copying backups. Unchanged mods reuse their previous archive; changed/new mods get a new immutable archive. Removed mods are omitted only from the new snapshot, not deleted from older snapshots.
- Change detection stores each mod's relative file paths, file sizes, creation/write timestamps, file attributes, directory inventory and directory attributes (directory timestamps are recorded for diagnostics but do not trigger file-content reads), plus a SHA-256 digest for each file.
- New archives are written to temporary files. A manifest is committed as complete only after all referenced archives are ready. If files are locked, the incomplete manifest is not offered for restore; Continue Backup retries the missing entries and then completes the manifest.
- Manifest version 1 and monolithic backups remain supported by restore. New UI backups use per-component archives; monolithic packaging is not offered because it defeats per-mod archive reuse.

### 4.4 ISafetyBackupService

- Automatically creates fast (`NoCompression`) zip archives of configs/mods before destructive actions (Purge or Restore).
- Archives are saved to the `SafetyBackups/` folder.
- Background task cleans up old safety backups older than `AppSettings.SafetyBackupRetentionDays`.

## 5. ViewModels Mapping (MVVM)

- **DashboardVM:** Manage `BackupProfiles`, bind UI to `CustomFolders` lists. Implement "Remap Drive Letters" command (bulk string replace D:\ to E:\ across all profiles).
- **SettingsVM:** Bind to `AppSettings`. Must include a DataGrid/List to visually edit `CompressionRules` (Size Limit input + ComboBox for CompressionLevel).
- **VanillaPurgeVM:** Commands for generating baseline snapshots and triggering Purge logic.
- **BackupRestoreVM:** Execute backups, handle resumable jobs state, display rich async progress bars for large IO operations.
