# Vortex Backup Tool - Current Implementation and Handoff

Updated 2026-11-01. This is the current source of truth for a new Copilot chat. Other files in Documentation contain original rationale, human plans and an illustrative mockup. The user runs the app with Visual Studio F5; do not launch or automate it.

This is a project handoff, not a verbatim transcript. It captures the important decisions and current state so work can continue without rediscovering or reimplementing completed features. The repository code and tests are authoritative if they differ from this document. Start by checking the worktree and the requested issue; do not replace the architecture or redo completed items below. The project is now in the **polish, finish remaining small functionality, and debug/validate** phase—not a fresh implementation or redesign phase. Keep changes surgical, preserve unrelated work, and run the narrowest relevant tests/build.

## Goal and original plan

Create a portable Windows backup tool primarily for Vortex modpacks, but flexible enough for other games and custom mod folders. The original implementation sequence was application shell and DI; Settings and profiles; Vanilla snapshots and safe purge; Backup & Restore with progress, ignore rules and retry; then logging and portable publishing. The legacy proof of concept's main weakness was a monolithic archive, requiring huge archives to be rebuilt for small mod updates. Incremental per-mod snapshots are now implemented.

## Implemented

- .NET 10 WPF, WPF-UI 4.3.0 (NuGet `WPF-UI`), CommunityToolkit.Mvvm, DI and layered Models/Interfaces/Services/ViewModels/UI.
- UI look (reworked 2026-10-31): Windows 11 Settings style pages plus Visual Studio 2026 style Output/status bar.
  - Pages are built from `controls:SettingsCard` rows (`UI/Controls/SettingsCard.cs`): icon, header, description, control on the right. `IsStacked="True"` puts the control below the text for wide inputs such as paths. Rows are grouped under `SectionHeaderTextStyle` headers.
  - All shared styles are in `UI/Styles/AppStyles.xaml`, merged in `App.xaml`: type ramp, page/header/card styles, `BoolToVisibility`, `MonospaceFontFamily`, Output list and status bar styles.
  - Theme: `AppSettings.Theme` (`AppTheme` System/Light/Dark, default System) is applied at startup via `IThemeService` (`UI/Services/ThemeService.cs`, WPF-UI `ApplicationThemeManager`/`SystemThemeWatcher`). The Settings "App theme" row applies and saves it immediately; it is not part of the Save button.
- Settings page: app theme, backup destination, safety backups, compression rules, retention and global ignore patterns.
- Profiles page (`DashboardPage`, nav label "Profiles"): JSON profiles, editable paths, new-profile default `%APPDATA%\Vortex`, per-path drive swap and Find on other disk. No global drive-letter rewrite.
- Every game list is sorted by game name (current culture, case-insensitive): Profiles page (`DashboardViewModel.ReplaceProfiles`, so also after add/edit/delete; the Find-on-other-disk path list follows it), Vanilla & Purge and the Backup & Restore picker (added 2026-11-01).
- Custom paths (added 2026-10-31): a profile has any number of custom path entries, each a folder **or a single file** (no wildcards), stored in `BackupProfile.CustomFolders` (name kept for JSON compatibility).
  - Editor: list rows (`CustomPathItemViewModel`: path, File/Folder/Not found, drive swap) with Add folder, Add files (multi-select `IFilePickerService`) and remove; duplicates are ignored.
  - Backup: each entry is its own unit and archive, reused when unchanged. Manifest units carry `CustomPath`, the exact entry: a folder, or a file whose `OriginalRootPath` is its parent folder. Unit keys use `CustomPath`, so two files in one folder never collide; older manifests keep matching by folder root. Single-file units have no directory fingerprints.
  - Restore: custom units match profile entries by exact path, then the same path on another drive, then a unique leaf name. A file entry is extracted into its parent folder. This fixes the old leaf-name collision between two folders with the same name.
- Link handling (added 2026-10-31), via `IFileSystemProvider.ScanFollowingLinks` / `ScanWithoutFollowingLinks` / `DeleteEntry` in `FileSystemProvider.cs` and `NativeFileInfo.cs`:
  - Vortex staging contains real files; links (hardlinks/symlinks) exist only where Vortex deploys them into game folders.
  - Backups (staging, AppData, custom paths) **follow links and archive the target bytes**:
    - file symlinks are read through their target, and their fingerprints use the target's size/times, so editing the target re-archives;
    - directory symlinks and junctions are followed once;
    - loops, folders already included through another path, and broken links are skipped and reported in Output.
  - Game folders (vanilla snapshot, diff, purge) are scanned **without following links**. Entries are classified as `FileEntryKind` File / HardLink (link count > 1, via `GetFileInformationByHandle`) / FileSymlink / DirectoryLink. Directory links are never recursed into. `FileMetadata.Kind` is stored in snapshots; older snapshots default to File.
  - Cloud placeholders (reparse points without a link target) are treated as ordinary files.
- Vanilla & Purge: per-profile persisted baseline snapshots, preview, confirmed purge and safety backup before deletion. Purge returns the game folder to the snapshot state:
  - Unregistered hardlinks, file symlinks and directory links/junctions are removed as links only; their targets and other hardlink names are never touched. The preview marks them, and Output reports how many links were removed.
  - Links get no safety copy because their data lives elsewhere, except a hardlinked file whose every name is being deleted, which gets one copy.
  - Creating a snapshot warns when the game folder already contains links (mods still deployed).
  - Empty folder cleanup (added 2026-11-01): `SnapshotData.Directories` records every real folder of the vanilla game (empty ones included; links are not entered). After deleting entries, Purge removes every real folder that is not vanilla, deepest first, once it is empty (`Directory.Delete` non-recursive, no safety copy; folders that already were empty are cleaned too). Vanilla folders = `Directories` plus every folder holding a snapshot entry. A folder that still has content (a file that could not be deleted) or cannot be removed is kept and counted. `ISnapshotService.GetUnregisteredDirectoriesAsync` feeds the preview (`"<folder>/    (folder: removed when empty)"`) and Purge; `PurgeResult` has `LinksRemoved`, `FoldersRemoved`, `FoldersKept`. Older snapshots (`Directories` null) only know folders holding vanilla files, so an originally empty vanilla folder counts as unregistered; the preview/purge warns and suggests recreating the snapshot **after** purging (recreating first would record leftover empty folders as vanilla).
  - Content hashes (added 2026-11-01): `CreateSnapshotAsync` stores a SHA-256 (`FileMetadata.Sha256`, uppercase hex) for every regular file, with throttled "Hashing game files" progress. Links are not hashed; unreadable files get no hash and the snapshot creation warns about them. Older snapshots have no hashes and still load.
- Game folder backup components (split 2026-11-01), both compared against the vanilla snapshot:
  - **Game configs** (`BackupComponent.GameConfigs`, folder `configs`, `configs.zip`): only **new** files, i.e. paths not in the snapshot, the same set the purge preview shows. A reinstall or file verification rewriting vanilla files therefore never turns the whole game into "configs". Linked entries are left out (Vortex deployments; their data is in the staging backup), with a warning that says Changed game files include them.
  - **Changed game files** (`BackupComponent.GameChanges`, `BackupOptions.IncludeGameChanges`, folder `changes`, one unit/archive `game_changes.zip`, monolithic key `changes`): a **complete, shareable copy of everything that differs from vanilla**: new files, configs, modified vanilla files and deployed mod files. Links are stored as **real files**: hardlinks/file symlinks are read through the link, directory links/junctions are expanded with `ScanFollowingLinks` (entries `link/relative`; broken links/loops reported in Output). Zip entry names are relative to the game folder and no link metadata is written, so the archive can be unpacked into a game folder on another PC. Not covered: files outside the game folder (add them as custom paths) and vanilla files that were deleted.
  - Modified-file detection with hashes (`ISnapshotService.CompareAsync(verifyContent: true)`, returns `SnapshotComparison`): same size/time/kind → unchanged without reading; different size or kind → changed; same size and kind but different time → the file is hashed and is unchanged when the hash matches the snapshot. Those files are "re-anchored": `ISnapshotStore.UpdateEntriesAsync` saves their new timestamps into the baseline (known entries only, the snapshot file keeps its creation time), so the next backup does not hash them again; Output reports the count. Files without a baseline hash are reported as modified and counted as unverified, with a warning to recreate the snapshot. `GetChangesAsync` (purge preview, Game configs) stays metadata-only.
  - Restore puts both into the game folder; they share the game-folder drive picker (`RestoreGameConfigsDrive`) and each has its own restore card.
- Ignore list = **game folder filter only** (user decision 2026-10-31, extended to Changed game files 2026-11-01; verified end to end in `IgnoreListTests.cs`):
  - Staging, Vortex AppData and custom paths are always backed up and restored **completely**, Vortex's own files included. The list only filters the game folder components (Game configs and Changed game files), the game-folder diff against the vanilla snapshot: there only real mod leftovers, configs and mod files are wanted, not Vortex's files.
  - Defaults (`AppSettings.VersionedDefaultIgnorePatterns`): `vortex.deployment*.json` (deployment manifests including the `vortex.deployment.<mod type>.json` variants, defaults version 1) and `__folder_managed_by_vortex` (folder markers, defaults version 2).
  - Existing settings files receive only the defaults newer than their `DefaultsVersion` (in `JsonSettingsStore.LoadAsync`), so a default the user removed is never re-added.
  - The profile switch "Filter game configs with the ignore list" (`BackupProfile.UseIgnoreList`) controls it. Output reports how many files were skipped, with up to 5 examples.
  - Restore applies the same patterns to Game configs and Changed game files units only (relative to the game folder), so older backups do not bring Vortex files back. It uses the matching profile's switch, defaulting to on.
  - Vanilla snapshots and Purge never use the ignore list, so Purge removes Vortex's files like any other unregistered entry.
  - The first backup after adding a pattern re-archives the configs unit once, because its file list changes.
- Log files (added 2026-10-31): `FileLogWriter` (`ILogFileWriter`) appends every Output entry to `Logs\log_yyyy-MM-dd.txt`. Logs older than 30 days are deleted on the first write of a session, and logging never throws. Unhandled UI, AppDomain and unobserved task exceptions are logged with full details; UI ones also appear in Output and do not close the app. The Output toolbar has "Open Logs folder"; Clear only empties the panel.
  - Per-file archive log, so slow files can be found and their compression rule adjusted. `ArchiveService` writes through `ILogFileWriter.AppendDetail`, to the log file only (never the Output panel):
    - `Archiving: <path> (<size>, level <level>)` when a file starts;
    - `Archived:  <path> in <s> s (<size>, <speed>/s, level <level>)` when it finishes;
    - `Skipped ...` / `Failed while reading ...` for missing and locked files.
  - Detail lines carry `[yyyy-MM-dd HH:mm:ss.fff]` timestamps and invariant number formatting. They are buffered and appended about once per second (or at 256 KB), with a final flush on dispose. The log file is never held open, so it can be read while the app runs.
  - Only files actually archived are logged (reused unchanged mods are not read). Measured overhead: +0.7% for 20,000 tiny files, the worst case; about 350 bytes per line.
- Portable publish (added 2026-10-31): `UI/Properties/PublishProfiles/Portable.pubxml` builds one self-contained single-file `UniversalBackupTool.UI.exe` for win-x64 (about 65 MB, native libraries self-extracted, compressed, no trimming). `Directory.Build.props` embeds symbols in Release builds, so the publish folder holds only the .exe and crash logs keep line numbers. Verified by publishing to a temp folder: the output is one .exe. Not yet launched from that output by the user.
  - Command: `dotnet publish UniversalBackupTool.UI/UniversalBackupTool.UI.csproj -p:PublishProfile=Portable`; in Visual Studio use Publish > Portable.
- App icon (added 2026-11-01): `UI/Assets/AppIcon.ico` (blue tile, white shield, download arrow; 16–256 px, PNG-compressed), generated by a System.Drawing script, not downloaded. `ApplicationIcon` in the csproj embeds it in the .exe (Explorer, shortcut; verified in a portable publish); it is also a WPF `Resource` used as `FluentWindow.Icon` (taskbar) and the `TitleBar` `ui:ImageIcon`.
- Install/Uninstall scripts (added 2026-11-01) in `UI/Scripts/`: `Install.bat`/`Uninstall.bat` are double-click wrappers for `Install.ps1`/`Uninstall.ps1` (UTF-8 **with BOM**, needed for the Cyrillic default path in Windows PowerShell 5.1; keep it when editing). Parameters `-InstallRoot` (default `D:\Programs`), `-BackupRoot` (default `G:\Мій диск\Software backups`), `-ShortcutFolder` (default Desktop; used for testing). App folder name `Vortex Backup Tool`, shortcut `Vortex Backup Tool.lnk`.
  - Install: publishes the Portable .exe from the repository into a temp folder (or takes `-SourceExe`), copies **only the .exe** into `D:\Programs\Vortex Backup Tool` (data next to it is never touched, so it doubles as an update), offers to restore data from the saved copy on a fresh install (robocopy, the newer .exe is kept), and creates the desktop shortcut. Refuses while the tool runs from the install folder.
  - Uninstall: refuses while the tool runs or when the backup drive is missing; asks for confirmation (`-Force` skips); mirrors (`robocopy /MIR`) the **whole** program folder, .exe included, to `<BackupRoot>\Vortex Backup Tool`; verifies file count and total size; only then deletes the shortcut and the program folder. The saved folder is a complete portable copy: copy it anywhere and run the .exe (paths inside the app folder are relative). Absolute paths outside it (backup destination, game/Vortex paths) must exist on the new PC or be changed with the per-path drive swap.
  - Tested 2026-11-01 against scratch folders (install, uninstall, reinstall with restore, missing backup drive) and the publish step; not yet run by the user against the real `D:\Programs` / `G:`.
- Portable settings paths: `JsonSettingsStore` saves `GoogleDriveBackupPath`/`SafetyBackupPath` relative when they are inside the folder of settings.json (the app folder) and resolves relative paths on load. Moving the app folder therefore keeps its own Backups/SafetyBackups working; paths elsewhere stay absolute.
- Backup & Restore profile picker is built for many games:
  - search box (`TargetFilter` → `VisibleTargets`);
  - a "Select all" button that becomes "Deselect all" once every listed game is selected (acts on the listed/filtered games only), plus Clear (all games);
  - after a backup runs to the end (also with locked files), the games are deselected but the component toggles keep the user's choice (no reset to defaults, changed 2026-11-01). A cancelled or failed backup keeps the games selected so it can be retried;
  - live "N of M selected" summary;
  - a bounded, scrollable three-column checkbox grid, with games sorted by name.
- Backup & Restore: Continue Backup for locked files, overwrite confirmation, pre-restore safety backup, component selection and legacy version-1/monolithic restore. Progress and cancel live in the global Output panel (below).
- Application-wide operations and Output panel (added 2026-10-31):
  - Long operations (Backup, Continue Backup, Restore, Create Snapshot, Purge Preview, Purge) run through the singleton `IOperationCoordinator`. Only one runs at a time, and it keeps running when the user switches pages. While one runs, operation buttons on every page are disabled through `BasicViewModel.CanStartOperation`. Short page actions (load, save profile/settings, browse) are not coordinated.
  - `TryBegin(name)` returns an `IOperationScope` (null if busy). The scope provides the `CancellationToken` and is an `IProgress<BackupProgress>` passed straight to services. It can be reported from any thread: the coordinator marshals to the UI `SynchronizationContext` captured at `TryBegin` and merges bursts of reports into one UI update. Each new `Phase` string is written once to Output (`"<Operation>: <phase>"`). Disposing the scope ends the operation.
  - `IOperationOutputService` holds the Output entries (`OutputEntry`: timestamp, `OutputSeverity` Info/Success/Warning/Error, message), capped at 5000 lines. It is thread-safe through the `SynchronizationContext` captured when DI creates it on the UI thread.
  - All user-facing messages go to Output. Pages no longer have Error/Warning/Done InfoBars or their own progress card. Page-specific content stays on the page: the paused-job card with Continue/Discard, purge preview list, snapshot status, restore plan and Find-on-other-disk results.
  - Layout: the navigation pane runs the full window height, with Settings at the bottom. Output and the status bar are docked at the bottom of the **page area only**:
    - they live in `NavigationView.ContentOverlay` (`BottomDock`);
    - `MainWindow.xaml.cs` sets the bottom `Margin` of the `NavigationViewContentPresenter` to the dock height, so pages end above it;
    - do not use `NavigationView.FrameMargin` for this: WPF-UI applies it to the container that also holds the overlay, which pushes the dock up;
    - a `Thumb` grip above the panel resizes it (minimum 90 px, and at least 220 px is kept for the page).
  - Output is a Visual Studio style tool window:
    - slim title strip with icon commands (Copy all, Clear, Hide);
    - flat monospace log using `FlatListBoxStyle`, with severity colours (`SystemFillColorSuccess/Caution/CriticalBrush`);
    - auto-scrolls only while already at the bottom; Ctrl+C copies the selected lines.
  - A VS-style status bar below it shows "Ready" or the operation and phase, the counters, a compact 120 px progress bar (indeterminate when totals are 0), the percentage, Cancel/Cancelling..., and an Output toggle. Since 2026-11-01 the counters (`MainViewModel.ProgressCountsText`, e.g. "93 / 2,337 files · 230.9 MB / 1.2 GB") are a separate, never-trimmed text next to the progress bar; the operation/phase text (`StatusText`) takes the remaining width in a `Grid` column and is trimmed with an ellipsis, and its tooltip (`StatusToolTip`) shows the full status plus the current file. Keep phase strings short (e.g. "archiving changes"). `MainViewModel.IsOutputVisible` hides the tool window and remembers its height; it reopens automatically when an operation starts.
  - Closing the window during an operation asks for confirmation, cancels the operation and closes once it has stopped (`MainViewModel.TryPrepareClose` / `CloseRequested`). This lets cancellation clean up temporary archives.
  - `ISnapshotService.PurgeUnregisteredFilesAsync` returns `PurgeResult(FilesDeleted, SafetyBackupPath)` plus `LinksRemoved`/`FoldersRemoved`/`FoldersKept`, so the actual counts and safety archive are reported.
- Incremental snapshots: each run creates a dated manifest per component under `<backup-root>\<game>\<component>\<timestamp>`, where component is `staging`, `configs`, `changes`, `appdata` or `custom`. Shared immutable archives live in the sibling `<backup-root>\<game>\<component>\_archives`. A restore selects one component's dated folder. Keep `_archives` with all dated snapshot folders when moving/copying. One archive unit per top-level staging mod; loose root files are separate. New/changed units receive new archives; unchanged archives are reused; removed mods are omitted only from new snapshots. The same unit layout applies to configs, Vortex AppData and custom folders.
- Fingerprints store relative paths, size, creation/last-write times, file attributes, directory inventory/attributes, and SHA-256 per file. Hashes are calculated during the normal archive read. Unchanged metadata reuses stored hashes and archives without rereading file contents. When metadata changes, the file is read and hashed during archive creation; identical content hashes discard the temporary archive and reuse the prior archive. A stable 30 GB file is not reread on every run. Edits preserving all compared metadata cannot be detected without rereading; the user chose to avoid rereading unchanged large files.
- Locked-file snapshots remain incomplete and unavailable for restore until Continue Backup succeeds. Job state records failed files and pending archive/manifest publication.
- UI defaults: no profile selected and **every component off** (staging, game configs, changed game files, Vortex AppData, custom paths; changed 2026-11-01 at the user's request). New backups do not offer monolithic packaging. Existing version-1 and monolithic snapshots remain restorable. The first run after old manifests rearchives units without saved hashes.
- Snapshot retention exists in code: after a complete backup, `BackupJobRunner.CleanupRetainedBackupsAsync` keeps the newest `MaxPreservedBackupsPerGame` (Settings, default 3) dated folders per component. It deletes older ones and any `_archives/*.zip` not referenced by a retained manifest. The user confirmed this retention is intended (see settled decisions). Rules:
  - Only complete snapshots count toward the limit.
  - Incomplete snapshots listed in the active job's `PendingManifestPaths` (awaiting Continue Backup) are always kept, together with the archives they reference. If the job file can't be read, all incomplete snapshots are kept.
  - Incomplete snapshots with no active job (discarded) are deleted.
  - Folders without a manifest are never touched.
  - An unreadable manifest keeps its folder and skips unused-archive deletion for that run, with a warning.
  - `.pending` temp archives are never deleted by retention, so a discarded job's temp files stay until removed manually.
- Safety backup retention compares UTC file times with UTC now; it previously mixed UTC and local time.
- Paused job file: `BackupJobStateService` (DI constructor) saves to `AppStoragePaths.ActiveBackupJobFilePath`, the same path the app loads from. It writes via a temporary file and atomic replace; an unreadable job file loads as "no paused job".
- Last checked 2026-11-01: solution build succeeded; 200 NUnit tests passed. Realistic F5 testing with the user's Vortex data and large files remains necessary.

## Settled decisions — do not reopen without asking

- Backups are **dated snapshots with shared per-game immutable archives**, not one ever-growing mutable “latest” archive and not a full re-archive of every mod on each run. Preserving old snapshots and making one-mod updates fast is a core requirement.
- Keep shared `_archives` when moving/copying a game's snapshots.
- Retention cleanup is intended: keep the newest `MaxPreservedBackupsPerGame` snapshots per component and delete `_archives` zips that no retained manifest references. Changes to it must stay reference-aware: never delete an archive that a retained snapshot uses.
- Avoid rereading unchanged large files on every backup. The chosen policy is metadata-first comparison and SHA-256 during reads already needed for archive creation; when relevant metadata changes, hash while creating a candidate archive and reuse the prior archive if content hashes match. Do not silently claim byte-for-byte detection for edits that preserve all compared metadata.
- On new profiles, default Vortex AppData to `%APPDATA%\Vortex`, but keep it editable. Do not select profiles by default in Backup & Restore. All backup components start off and are opt-in; a finished backup deselects the games but never resets the component toggles (user decision 2026-11-01).
- Game lists are sorted by game name on every page.
- Game configs = new files only (paths missing from the snapshot, like the purge preview). Changed game files = every difference (new + content-modified files) as one self-contained archive with links stored as real files, so it can be shared and unpacked into another game folder (user decision 2026-11-01).
- Modified game files are judged by content: the snapshot stores SHA-256 hashes, rewritten-but-identical files are not "changed", and their new timestamps are saved into the baseline so they are hashed only once.
- Drive/path detection is per path. There is no global drive-letter remap; preserve the per-path drive swap and Find on other disk proposal flow.
- A locked-file backup must offer **Continue Backup**. Continue retries recorded failed files and completes pending archives/manifests; do not replace it with a warning-only experience or silently discard job state.
- Preserve restore compatibility with older version-1 and monolithic backups, even though the current UI creates incremental snapshots.
- User performs visual validation by running the app with F5. Do not start the UI, modify the user's game data, or automate their F5 session.
- Operations must not depend on the visible page. All page ViewModels and Pages are DI singletons. New long-running work goes through `RunOperationAsync` (coordinator), not a page-local `CancellationTokenSource` or progress card.
- All status, warning and error text goes to the Output panel through `ReportInfo/ReportSuccess/ReportWarning/ReportError` in `BasicViewModel`. Do not re-add per-page message InfoBars. Validate input and ask for confirmation **before** calling `RunOperationAsync`, so "X started." is only logged for real runs. The exception is Purge, whose confirmation needs the scan result.
- Links:
  - Backups follow links and store target bytes.
  - Game-folder operations never follow links.
  - Purge removes unregistered links (hardlinks, symlinks, junctions) as links only and must never modify or delete their targets. It removes non-vanilla folders only when they are empty (non-recursive delete).
  - Game configs skip linked files; Changed game files store them as real files.
  - Do not use recursive deletes or recursive enumeration through directory links in game folders.
- Custom paths are folders or single files, without wildcards.
- The ignore list only filters the game folder components (Game configs and Changed game files). Staging, Vortex AppData and custom paths are backed up and restored completely; do not apply the ignore list to them.

## Architecture and source map

The solution is layered; keep logic in the existing layer and follow its interfaces/DI patterns. Project names/namespaces are singular in places—check actual project references rather than “cleaning them up.”

- `UniversalBackupTool.Models` (`net10.0`): domain/data types, including `Models/Backups/*`, profiles, settings, snapshots and `AppStoragePaths`.
- `UniversalBackupTool.Interfaces` (`net10.0`): service contracts, references Models.
- `UniversalBackupTool.Services` (`net10.0`): service implementations, references Interfaces and Models. The active backup implementation is under `Services/Backups`.
- `UniversalBackupTool.ViewModels` (`net10.0`): CommunityToolkit.Mvvm ViewModels; references Interfaces.
- `UniversalBackupTool.UI` (`net10.0-windows`, WPF): views, navigation, dialogs and DI composition in `App.xaml.cs`; references Interfaces, Services and ViewModels. Uses WPF-UI 4.3.0 and Microsoft.Extensions.DependencyInjection 10.0.14.
- `UniversalBackupTool.Tests` (`net10.0`, NUnit 5): service and ViewModel tests; references Interfaces, Models, Services and ViewModels (ViewModels reference approved by the user 2026-10-31). Link tests create real hardlinks (Win32), junctions (`mklink /J`) and symlinks; symlink tests skip themselves when the machine does not allow creating symlinks.

Key implementation files:

- `UniversalBackupTool.Services/Services/Backups/BackupJobRunner.cs`: current `IBackupJobRunner`; snapshot construction/reuse, fingerprints, cancellation and retry/pending publication.
- `UniversalBackupTool.Services/Services/Backups/ArchiveService.cs`: ZIP streaming, compression rules, failure tracking and SHA-256 calculation during archive reads.
- `UniversalBackupTool.Services/Services/Backups/RestoreService.cs`: archive/manifest resolution, legacy formats, restore safety archive and extraction path validation.
- `UniversalBackupTool.Services/Services/Jobs/BackupJobStateService.cs`: persisted active job state.
- `UniversalBackupTool.Services/Services/FileSystem/FileSystemProvider.cs` and `NativeFileInfo.cs`: link-following (backup) and non-following (game folder) scans, link classification and link-only deletes.
- `UniversalBackupTool.Services/Services/Logging/FileLogWriter.cs`: daily log files; error handlers are registered in `UI/App.xaml.cs`.
- `UniversalBackupTool.Services/Services/Snapshots/SnapshotService.cs` and `JsonSnapshotStore.cs`: vanilla baseline (hashes, folders), metadata diff, content-verified comparison, purge with empty folder cleanup, persistence and timestamp re-anchoring.
- `UniversalBackupTool.Services/Services/Profiles/JsonProfileStore.cs`, `Services/Settings/JsonSettingsStore.cs`, `Services/Paths/PathDriveService.cs`: profile/settings persistence and per-path drive discovery.
- `UniversalBackupTool.Services/Services/Operations/OperationCoordinator.cs` and `OperationOutputService.cs`: application-wide operation state/progress/cancellation and the Output entries. Contracts are in `Interfaces/Interfaces/Operations/` (`IOperationCoordinator`, `IOperationScope`, `IOperationOutputService`), models in `Models/Models/Operations/`.
- `UniversalBackupTool.ViewModels/ViewModels/Base/BasicViewModel.cs`: base for all ViewModels. Takes the Output service and coordinator and provides `Report*`, `RunBusyAsync` (short page actions) and `RunOperationAsync` (coordinated long operations), plus `IsIdle`/`CanStartOperation`.
- `UniversalBackupTool.ViewModels/ViewModels/Main/MainViewModel.cs` + `UniversalBackupTool.UI/MainWindow.xaml(.cs)`: Output panel, global progress/cancel, close guard. `UI/Helpers/OutputListBehavior.cs`: auto-scroll and Ctrl+C copy for the Output list.
- `UniversalBackupTool.ViewModels/ViewModels/BackupRestore/BackupRestoreViewModel.cs`: backup/restore UI behavior and defaults; `Dashboard/DashboardViewModel.cs`, `VanillaPurge/VanillaPurgeViewModel.cs`, and `Settings/SettingsViewModel.cs` cover other pages.
- `UniversalBackupTool.UI/Views/*.xaml`: actual UI. The HTML in Documentation is illustrative only.
- Tests:
  - `BackupRestoreTests.cs`: incremental backup, restore, continue, retention and path safety;
  - `LinkHandlingTests.cs`: real links, purge, link-following backups, custom single-file paths, Changed game files storing links as real files;
  - `IgnoreListTests.cs`: ignore list end to end, Game configs (new only) versus Changed game files (new + modified), restore of Changed game files;
  - `SnapshotContentHashTests.cs`: snapshot hashes, content-verified comparison, re-anchoring, store updates keeping the creation time;
  - `PurgeFolderTests.cs`: recorded folders, empty folder removal, older snapshots without folders;
  - `LoggingTests.cs`, `PortableSettingsTests.cs`, `OperationServicesTests.cs`;
  - `ViewModelTests.cs`: profile picker toggle, components off by default and kept after a backup, operation rejection, theme save, custom paths editor;
  - `ServiceTests.cs`, `ProfileStoreTests.cs`, `SnapshotStoreTests.cs`, `PathDriveServiceTests.cs`.

There are empty (0-byte) leftover files still tracked in git: `UniversalBackupTool.Models/Models/*.cs` (root level), `UniversalBackupTool.Services/Interfaces/*.cs`, `UniversalBackupTool.Interfaces/UniversalBackupTool.Interfaces.csproj` and `UniversalBackupTool.Tests/ServiceLayerTests.cs`. `Services/Implementations/*` no longer exists. Legacy `IBackupEngine`/`BackupEngine` is still registered in DI. The UI is wired to `IBackupJobRunner`/`BackupJobRunner`. Do not delete or migrate legacy code without checking references and tests; legacy restore compatibility is intentional.

## Remaining work / recommended order

1. User-run F5 validation:
   - Backups: first backup; an unchanged second run writes zero mod files; one-mod update; metadata touch with an equal hash; mod removal; old/new restore; locked files and resume after an app restart; ignore patterns; large files; binding errors.
   - UI:
     - switching tabs during a backup keeps progress;
     - operation buttons are disabled on other pages;
     - Cancel and closing the window during an operation;
     - light/dark themes;
     - Output auto-scroll and copy.
   - New:
     - Purge on a game with Vortex deployments (links removed, staging intact);
     - snapshot warning when mods are deployed;
     - custom paths with files and folders;
     - log files and Open Logs folder;
     - launching the portable .exe from a fresh folder;
     - real Install (D:\Programs, desktop shortcut, icon) and Uninstall (saved copy in Google Drive), then running the saved copy from another folder/PC.
   - Added 2026-11-01:
     - purge with an older snapshot removes leftover empty folders; then recreate the snapshot (records folders and hashes; snapshot creation is slower because it hashes the game);
     - Changed game files after a same-version reinstall skip identical files and report re-anchored timestamps; a second run hashes nothing;
     - Changed game files archive unpacked into a clean game folder gives a working modded game (links as real files);
     - status bar counters stay visible with long phase texts and at the minimum window width;
     - sorted game lists; toggles kept after a backup.
2. Optional: "Export for sharing": one zip with Changed game files plus custom paths under a clear name, and a text list of deleted vanilla files.
3. Optional: clean up `.pending` temp archives left by discarded jobs (reference-aware; never touch an active job's pending files).
4. Optional: Continue Backup reopens pending archives with `ZipArchiveMode.Update`, which rewrites the archive and can be slow or memory-heavy for multi-GB mods; consider a streaming alternative.
5. Optional: benchmark and verify Google Drive sync locations and non-NTFS filesystems (hardlink detection needs NTFS; elsewhere files are treated as ordinary).

## Implemented versus not yet proven

**Implemented and covered by service tests:** per-mod incremental reuse; changed/removed mod snapshots; metadata-change/content-same hash reuse; restore from shared archives; legacy restore; locked-file retry at the service/job level; ZIP traversal rejection; profile/settings/snapshot storage; vanilla diff and purge safety backup; operation coordinator (single operation, cancel, scope end, phase logging) and Output service (severity, cap, clear). Also covered:
- real hardlinks, junctions and symlinks: classification, link-only purge with targets intact, link-following backups with loop and broken-link handling, symlink target changes re-archived;
- custom single-file paths: backup and restore;
- Game configs versus Changed game files, links stored as real files, snapshot hashes with re-anchoring, empty folder purge;
- log files, portable settings paths;
- ViewModel behaviour.

The last reported automated test run was 200 passing NUnit tests on 2026-11-01. Headless XAML smoke checks (pages and window built without being shown) found no XAML or binding errors.

**Not established by those unit tests:**
- the Output panel and cross-page operation behaviour in the real WPF app, including UI-thread marshalling under a real dispatcher;
- a real app crash followed by process restart and successful resume;
- actual locked files under Vortex/antivirus load;
- real modpack-scale performance and disk-space behavior;
- WPF visual correctness in all pages;
- running the published single-file .exe;
- purge against a real Vortex deployment;
- retention cleanup against real backup folders (service coverage: `Backup_MultiComponent_UsesSeparateComponentFoldersAndRetentionPerComponent` and the `Retention_*` tests in `BackupRestoreTests.cs`).

Treat these as manual/integration validation, not as already verified.

The legacy `IFileSystemProvider.EnumerateFilesAsync`/`IsSymlink`/`ResolveActualPath` remain only for the legacy `BackupEngine`; new code uses the scan methods.

## Constraints and implementation notes

- App data is rooted beside the executable via `AppStoragePaths`: settings.json, profiles.json, Logs, Temp/active_backup_job.json, SafetyBackups and Snapshots. In the single-file publish `AppContext.BaseDirectory` is the .exe folder, so this stays portable. `%APPDATA%\Vortex` is external Vortex source data, not app storage.
- Use local time (`DateTime.Now`) for user-facing timestamps.
- Do not change namespaces, project files or folder structure unless asked. Preserve unrelated work and existing line endings.
- Every page uses `Style="{StaticResource AppPageStyle}"`, which sets the theme foreground and `ScrollViewer.CanContentScroll="False"`. Pages follow this layout:
  - their own `<ScrollViewer VerticalScrollBarVisibility="Auto">`;
  - inside it, a `Grid` with one `<ColumnDefinition Width="*" MaxWidth="1072" />` (left aligned, capped width);
  - inside that, a `StackPanel Style="{StaticResource PageContentPanelStyle}"`.
- Text must use the `AppStyles.xaml` text styles (`PageTitleTextStyle`, `SectionHeaderTextStyle`, `BodyTextStyle`, `CaptionTextStyle`, `PathTextStyle`, ...) or set `Foreground` from a theme brush. The inherited default foreground is black, which caused unreadable titles in dark mode.
- Nested scrollable lists inside a page: give them a `MaxHeight`, enable virtualization (`CanContentScroll="True"` on the inner ScrollViewer) and set `helpers:NestedScrollBehavior.PassWheelAtEdges="True"`.
- Avoid `ui:Button` in DataTemplates; a previous `PressedForeground` binding error was resolved with plain WPF `Button`. Use `helpers:StackPanelSpacing.Spacing`, not a nonexistent `ui:StackPanel`.
- Test actual crash/restart resume of a paused backup.
- Legacy `IBackupEngine`/`BackupEngine` remains beside newer `IBackupJobRunner`/`BackupJobRunner`; UI uses the latter. Check references before removing old code.
- Purge deletes files and links, then the non-vanilla folders that are empty afterwards. Safety archives run before purge/restore overwrites (empty folders need none).
- For a future code task, first inspect the exact code path and its existing tests, then implement the requested polish/fix; do not use this handoff as a reason to perform a broad audit or unrelated refactor.

## Other Documentation files

- `Basic info.txt`: original rationale (Ukrainian), updated with incremental backup, retry behavior, link handling, custom paths, logging, portable publish, the Game configs / Changed game files split, snapshot hashes, empty folder purge and the Install/Uninstall scripts.
- `Vortex_Backup_Tool_Detailed_Human_Plan.docx`: original human-readable plan, retained as historical design input; this Markdown contains current status.
- `Vortex_Backup_Tool_Mockup.html`: illustrative UI updated with current defaults and labels; it is not the live UI specification.
