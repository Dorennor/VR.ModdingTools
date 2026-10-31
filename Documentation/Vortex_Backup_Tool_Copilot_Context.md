# Vortex Backup Tool - Current Implementation and Handoff

Updated 2026-10-31. This is the current source of truth for a new Copilot chat. Other files in Documentation contain original rationale, human plans and an illustrative mockup. The user runs the app with Visual Studio F5; do not launch or automate it.

This is a project handoff, not a verbatim transcript. It captures the important decisions and current state so work can continue without rediscovering or reimplementing completed features. The repository code and tests are authoritative if they differ from this document. Start by checking the worktree and the requested issue; do not replace the architecture or redo completed items below. The project is now in the **polish, finish remaining small functionality, and debug/validate** phase—not a fresh implementation or redesign phase. Keep changes surgical, preserve unrelated work, and run the narrowest relevant tests/build.

## Goal and original plan

Create a portable Windows backup tool primarily for Vortex modpacks, but flexible enough for other games and custom mod folders. The original implementation sequence was application shell and DI; Settings and profiles; Vanilla snapshots and safe purge; Backup & Restore with progress, ignore rules and retry; then logging and portable publishing. The legacy proof of concept's main weakness was a monolithic archive, requiring huge archives to be rebuilt for small mod updates. Incremental per-mod snapshots are now implemented.

## Implemented

- .NET 10 WPF, WPF-UI 4.3.0 (NuGet `WPF-UI`), CommunityToolkit.Mvvm, DI and layered Models/Interfaces/Services/ViewModels/UI.
- Settings page: backup destination, compression rules, safety-backup retention and global ignore patterns.
- Dashboard: JSON profiles, editable paths/custom folders, new-profile default `%APPDATA%\Vortex`, per-path drive swap and Find on other disk. No global drive-letter rewrite.
- Vanilla & Purge: per-profile persisted baseline snapshots, preview, confirmed purge and safety backup before deletion.
- Backup & Restore: progress/cancel, Continue Backup for locked files, overwrite confirmation, pre-restore safety backup, component selection and legacy version-1/monolithic restore.
- Incremental snapshots: each run creates a dated manifest under `<backup-root>\<game>\<timestamp>`. Shared immutable archives live in sibling `<backup-root>\<game>\_archives`. Keep `_archives` with all dated snapshot folders when moving/copying. One archive unit per top-level staging mod; loose root files are separate. New/changed units receive new archives; unchanged archives are reused; removed mods are omitted only from new snapshots. The same unit layout applies to configs, Vortex AppData and custom folders.
- Fingerprints store relative paths, size, creation/last-write times, file attributes, directory inventory/attributes, and SHA-256 per file. Hashes are calculated during the normal archive read. Unchanged metadata reuses stored hashes and archives without rereading file contents. When metadata changes, the file is read and hashed during archive creation; identical content hashes discard the temporary archive and reuse the prior archive. A stable 30 GB file is not reread on every run. Edits preserving all compared metadata cannot be detected without rereading; the user chose to avoid rereading unchanged large files.
- Locked-file snapshots remain incomplete and unavailable for restore until Continue Backup succeeds. Job state records failed files and pending archive/manifest publication.
- UI defaults: no profile selected, staging and Vortex AppData selected, game configs and custom folders unselected. New backups do not offer monolithic packaging. Existing version-1 and monolithic snapshots remain restorable. The first run after old manifests rearchives units without saved hashes.
- Last checked 2026-10-31: solution build succeeded; 131 NUnit tests passed. Realistic F5 testing with the user's Vortex data and large files remains necessary.

## Settled decisions — do not reopen without asking

- Backups are **dated snapshots with shared per-game immutable archives**, not one ever-growing mutable “latest” archive and not a full re-archive of every mod on each run. Preserving old snapshots and making one-mod updates fast is a core requirement.
- Keep shared `_archives` when moving/copying a game's snapshots. Do not add archive cleanup/retention that can break any still-retained snapshot; garbage collection needs a safe reference-aware design.
- Avoid rereading unchanged large files on every backup. The chosen policy is metadata-first comparison and SHA-256 during reads already needed for archive creation; when relevant metadata changes, hash while creating a candidate archive and reuse the prior archive if content hashes match. Do not silently claim byte-for-byte detection for edits that preserve all compared metadata.
- On new profiles, default Vortex AppData to `%APPDATA%\Vortex`, but keep it editable. Do not select profiles by default in Backup & Restore. Default selected components are staging and Vortex AppData only; game configs and custom folders are opt-in.
- Drive/path detection is per path. There is no global drive-letter remap; preserve the per-path drive swap and Find on other disk proposal flow.
- A locked-file backup must offer **Continue Backup**. Continue retries recorded failed files and completes pending archives/manifests; do not replace it with a warning-only experience or silently discard job state.
- Preserve restore compatibility with older version-1 and monolithic backups, even though the current UI creates incremental snapshots.
- User performs visual validation by running the app with F5. Do not start the UI, modify the user's game data, or automate their F5 session.

## Architecture and source map

The solution is layered; keep logic in the existing layer and follow its interfaces/DI patterns. Project names/namespaces are singular in places—check actual project references rather than “cleaning them up.”

- `UniversalBackupTool.Models` (`net10.0`): domain/data types, including `Models/Backups/*`, profiles, settings, snapshots and `AppStoragePaths`.
- `UniversalBackupTool.Interfaces` (`net10.0`): service contracts, references Models.
- `UniversalBackupTool.Services` (`net10.0`): service implementations, references Interfaces and Models. The active backup implementation is under `Services/Backups`.
- `UniversalBackupTool.ViewModels` (`net10.0`): CommunityToolkit.Mvvm ViewModels; references Interfaces.
- `UniversalBackupTool.UI` (`net10.0-windows`, WPF): views, navigation, dialogs and DI composition in `App.xaml.cs`; references Interfaces, Services and ViewModels. Uses WPF-UI 4.3.0 and Microsoft.Extensions.DependencyInjection 10.0.14.
- `UniversalBackupTool.Tests` (`net10.0`, NUnit 5): service-level tests; references Interfaces, Models and Services, **not ViewModels**. Ask before adding a ViewModels reference or expanding test architecture.

Key implementation files:

- `UniversalBackupTool.Services/Services/Backups/BackupJobRunner.cs`: current `IBackupJobRunner`; snapshot construction/reuse, fingerprints, cancellation and retry/pending publication.
- `UniversalBackupTool.Services/Services/Backups/ArchiveService.cs`: ZIP streaming, compression rules, failure tracking and SHA-256 calculation during archive reads.
- `UniversalBackupTool.Services/Services/Backups/RestoreService.cs`: archive/manifest resolution, legacy formats, restore safety archive and extraction path validation.
- `UniversalBackupTool.Services/Services/Jobs/BackupJobStateService.cs`: persisted active job state.
- `UniversalBackupTool.Services/Services/FileSystem/FileSystemProvider.cs`: file enumeration and current symlink behavior.
- `UniversalBackupTool.Services/Services/Snapshots/SnapshotService.cs` and `JsonSnapshotStore.cs`: vanilla baseline/diff persistence.
- `UniversalBackupTool.Services/Services/Profiles/JsonProfileStore.cs`, `Services/Settings/JsonSettingsStore.cs`, `Services/Paths/PathDriveService.cs`: profile/settings persistence and per-path drive discovery.
- `UniversalBackupTool.ViewModels/ViewModels/BackupRestore/BackupRestoreViewModel.cs`: backup/restore UI behavior and defaults; `Dashboard/DashboardViewModel.cs`, `VanillaPurge/VanillaPurgeViewModel.cs`, and `Settings/SettingsViewModel.cs` cover other pages.
- `UniversalBackupTool.UI/Views/*.xaml`: actual UI. The HTML in Documentation is illustrative only.
- `UniversalBackupTool.Tests/BackupRestoreTests.cs`: incremental backup/restore/continue and path-safety coverage; `ServiceTests.cs`, `ProfileStoreTests.cs`, `SnapshotStoreTests.cs`, and `PathDriveServiceTests.cs` cover related service behavior.

There are older duplicate-looking `UniversalBackupTool.Services/Implementations/*` and `UniversalBackupTool.Services/Interfaces/*` files plus `IBackupEngine`/`BackupEngine`. The UI is wired to `IBackupJobRunner`/`BackupJobRunner`. Do not delete or migrate legacy code without checking references and tests; legacy restore compatibility is intentional.

## Remaining work / recommended order

1. Symlink/reparse point correctness is a functional gap: file enumeration skips symlink files; backup does not resolve them to target bytes. Validate directory symlink traversal, implement safely, and test.
2. User-run F5 validation: first backup; unchanged second run writes zero mod files; one-mod update; metadata touch with equal hash; mod removal; old/new restore; locked files and resume after app restart; ignore patterns; large files; binding errors.
3. Structured logging to `Logs/`, including rotation/retention and explicit error reporting.
4. Configure and verify self-contained single-file portable publishing for .NET 10.
5. ViewModel tests; ask before adding a ViewModels project reference to Tests.
6. Consider safe garbage collection for unreferenced archives without breaking old snapshots.
7. Optional: benchmark and verify Google Drive sync locations and non-NTFS filesystems.

## Implemented versus not yet proven

**Implemented and covered by service tests:** per-mod incremental reuse; changed/removed mod snapshots; metadata-change/content-same hash reuse; restore from shared archives; legacy restore; locked-file retry at the service/job level; ZIP traversal rejection; profile/settings/snapshot storage; vanilla diff and purge safety backup. The last reported automated test run was 131 passing NUnit tests on 2026-10-31.

**Not established by those unit tests:** a real app crash followed by process restart and successful resume; actual locked files under Vortex/antivirus load; real modpack-scale performance and disk-space behavior; WPF binding/visual correctness in all pages; self-contained portable publish; correctness for symlinks/reparse points; or safe shared-archive cleanup. Treat these as manual/integration validation or open work, not as already verified.

**Current code-confirmed symlink limitation:** `FileSystemProvider.EnumerateFilesAsync` uses recursive enumeration then excludes files for which `IsSymlink` reports a reparse point. `IsSymlink` currently checks `File.Exists`, catches exceptions and returns false; `ResolveActualPath` exists but is not wired into backup enumeration/archive input. Directory links and junction behavior still need explicit testing. Any fix must avoid recursive traversal loops, escaping the selected source, duplicating targets, and accidentally archiving link metadata instead of target bytes.

## Constraints and implementation notes

- App data is rooted beside the executable via `AppStoragePaths`: settings.json, profiles.json, Logs, Temp/active_backup_job.json, SafetyBackups and Snapshots. Single-file publishing is not yet verified. `%APPDATA%\Vortex` is external Vortex source data, not app storage.
- Use local time (`DateTime.Now`) for user-facing timestamps.
- Do not change namespaces, project files or folder structure unless asked. Preserve unrelated work and existing line endings.
- Every WPF page needs `ScrollViewer.CanContentScroll="False"` and its own `<ScrollViewer VerticalScrollBarVisibility="Auto">` for page-wide wheel scrolling.
- Avoid `ui:Button` in DataTemplates; a previous `PressedForeground` binding error was resolved with plain WPF `Button`. Use `helpers:StackPanelSpacing.Spacing`, not a nonexistent `ui:StackPanel`.
- `BackupJobStateService.SaveAsync` uses `AppContext.BaseDirectory\Temp\active_backup_job.json`; test actual crash/restart resume.
- Legacy `IBackupEngine`/`BackupEngine` remains beside newer `IBackupJobRunner`/`BackupJobRunner`; UI uses the latter. Check references before removing old code.
- Purge deletes files, not empty directories. Safety archives run before purge/restore overwrites.
- For a future code task, first inspect the exact code path and its existing tests, then implement the requested polish/fix; do not use this handoff as a reason to perform a broad audit or unrelated refactor.

## Other Documentation files

- `Basic info.txt`: original rationale, updated with current incremental backup, retry behavior and symlink limitation.
- `Vortex_Backup_Tool_Detailed_Human_Plan.docx`: original human-readable plan, retained as historical design input; this Markdown contains current status.
- `Vortex_Backup_Tool_Mockup.html`: illustrative UI updated with current defaults and labels; it is not the live UI specification.
