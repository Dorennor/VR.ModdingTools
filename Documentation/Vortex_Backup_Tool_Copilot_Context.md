# Vortex Backup Tool - Current Implementation and Handoff

Updated 2026-10-31. This is the current source of truth for a new Copilot chat. Other files in Documentation contain original rationale, human plans and an illustrative mockup. The user runs the app with Visual Studio F5; do not launch or automate it.

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
- Dashboard: JSON profiles, editable paths/custom folders, new-profile default `%APPDATA%\Vortex`, per-path drive swap and Find on other disk. No global drive-letter rewrite.
- Vanilla & Purge: per-profile persisted baseline snapshots, preview, confirmed purge and safety backup before deletion.
- Backup & Restore profile picker is built for many games:
  - search box (`TargetFilter` → `VisibleTargets`);
  - a "Select all" button that becomes "Deselect all" once every listed game is selected (acts on the listed/filtered games only), plus Clear (all games);
  - after a backup runs to the end (also with locked files), no games are selected and components reset to the defaults (staging + Vortex AppData). A cancelled or failed backup keeps the selection so it can be retried;
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
  - A VS-style status bar below it shows "Ready" or the operation and phase, a compact progress bar (indeterminate when totals are 0), the percentage, Cancel/Cancelling..., and an Output toggle. `MainViewModel.IsOutputVisible` hides the tool window and remembers its height; it reopens automatically when an operation starts.
  - Closing the window during an operation asks for confirmation, cancels the operation and closes once it has stopped (`MainViewModel.TryPrepareClose` / `CloseRequested`). This lets cancellation clean up temporary archives.
  - `ISnapshotService.PurgeUnregisteredFilesAsync` returns `PurgeResult(FilesDeleted, SafetyBackupPath)` so the actual deleted count and safety archive are reported.
- Incremental snapshots: each run creates a dated manifest per component under `<backup-root>\<game>\<component>\<timestamp>`, where component is `staging`, `configs`, `appdata` or `custom`. Shared immutable archives live in the sibling `<backup-root>\<game>\<component>\_archives`. A restore selects one component's dated folder. Keep `_archives` with all dated snapshot folders when moving/copying. One archive unit per top-level staging mod; loose root files are separate. New/changed units receive new archives; unchanged archives are reused; removed mods are omitted only from new snapshots. The same unit layout applies to configs, Vortex AppData and custom folders.
- Fingerprints store relative paths, size, creation/last-write times, file attributes, directory inventory/attributes, and SHA-256 per file. Hashes are calculated during the normal archive read. Unchanged metadata reuses stored hashes and archives without rereading file contents. When metadata changes, the file is read and hashed during archive creation; identical content hashes discard the temporary archive and reuse the prior archive. A stable 30 GB file is not reread on every run. Edits preserving all compared metadata cannot be detected without rereading; the user chose to avoid rereading unchanged large files.
- Locked-file snapshots remain incomplete and unavailable for restore until Continue Backup succeeds. Job state records failed files and pending archive/manifest publication.
- UI defaults: no profile selected, staging and Vortex AppData selected, game configs and custom folders unselected. New backups do not offer monolithic packaging. Existing version-1 and monolithic snapshots remain restorable. The first run after old manifests rearchives units without saved hashes.
- Snapshot retention exists in code: after a complete backup, `BackupJobRunner.CleanupRetainedBackupsAsync` keeps the newest `MaxPreservedBackupsPerGame` (Settings, default 3) dated folders per component. It deletes older ones and any `_archives/*.zip` not referenced by a retained manifest. The user confirmed this retention is intended (see settled decisions). Rules:
  - Only complete snapshots count toward the limit.
  - Incomplete snapshots listed in the active job's `PendingManifestPaths` (awaiting Continue Backup) are always kept, together with the archives they reference. If the job file can't be read, all incomplete snapshots are kept.
  - Incomplete snapshots with no active job (discarded) are deleted.
  - Folders without a manifest are never touched.
  - An unreadable manifest keeps its folder and skips unused-archive deletion for that run, with a warning.
  - `.pending` temp archives are never deleted by retention, so a discarded job's temp files stay until removed manually.
- Last checked 2026-10-31: solution build succeeded; 148 NUnit tests passed. Realistic F5 testing with the user's Vortex data and large files remains necessary.

## Settled decisions — do not reopen without asking

- Backups are **dated snapshots with shared per-game immutable archives**, not one ever-growing mutable “latest” archive and not a full re-archive of every mod on each run. Preserving old snapshots and making one-mod updates fast is a core requirement.
- Keep shared `_archives` when moving/copying a game's snapshots.
- Retention cleanup is intended: keep the newest `MaxPreservedBackupsPerGame` snapshots per component and delete `_archives` zips that no retained manifest references. Changes to it must stay reference-aware: never delete an archive that a retained snapshot uses.
- Avoid rereading unchanged large files on every backup. The chosen policy is metadata-first comparison and SHA-256 during reads already needed for archive creation; when relevant metadata changes, hash while creating a candidate archive and reuse the prior archive if content hashes match. Do not silently claim byte-for-byte detection for edits that preserve all compared metadata.
- On new profiles, default Vortex AppData to `%APPDATA%\Vortex`, but keep it editable. Do not select profiles by default in Backup & Restore. Default selected components are staging and Vortex AppData only; game configs and custom folders are opt-in.
- Drive/path detection is per path. There is no global drive-letter remap; preserve the per-path drive swap and Find on other disk proposal flow.
- A locked-file backup must offer **Continue Backup**. Continue retries recorded failed files and completes pending archives/manifests; do not replace it with a warning-only experience or silently discard job state.
- Preserve restore compatibility with older version-1 and monolithic backups, even though the current UI creates incremental snapshots.
- User performs visual validation by running the app with F5. Do not start the UI, modify the user's game data, or automate their F5 session.
- Operations must not depend on the visible page. All page ViewModels and Pages are DI singletons. New long-running work goes through `RunOperationAsync` (coordinator), not a page-local `CancellationTokenSource` or progress card.
- All status, warning and error text goes to the Output panel through `ReportInfo/ReportSuccess/ReportWarning/ReportError` in `BasicViewModel`. Do not re-add per-page message InfoBars. Validate input and ask for confirmation **before** calling `RunOperationAsync`, so "X started." is only logged for real runs. The exception is Purge, whose confirmation needs the scan result.

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
- `UniversalBackupTool.Services/Services/Operations/OperationCoordinator.cs` and `OperationOutputService.cs`: application-wide operation state/progress/cancellation and the Output entries. Contracts are in `Interfaces/Interfaces/Operations/` (`IOperationCoordinator`, `IOperationScope`, `IOperationOutputService`), models in `Models/Models/Operations/`.
- `UniversalBackupTool.ViewModels/ViewModels/Base/BasicViewModel.cs`: base for all ViewModels. Takes the Output service and coordinator and provides `Report*`, `RunBusyAsync` (short page actions) and `RunOperationAsync` (coordinated long operations), plus `IsIdle`/`CanStartOperation`.
- `UniversalBackupTool.ViewModels/ViewModels/Main/MainViewModel.cs` + `UniversalBackupTool.UI/MainWindow.xaml(.cs)`: Output panel, global progress/cancel, close guard. `UI/Helpers/OutputListBehavior.cs`: auto-scroll and Ctrl+C copy for the Output list.
- `UniversalBackupTool.ViewModels/ViewModels/BackupRestore/BackupRestoreViewModel.cs`: backup/restore UI behavior and defaults; `Dashboard/DashboardViewModel.cs`, `VanillaPurge/VanillaPurgeViewModel.cs`, and `Settings/SettingsViewModel.cs` cover other pages.
- `UniversalBackupTool.UI/Views/*.xaml`: actual UI. The HTML in Documentation is illustrative only.
- `UniversalBackupTool.Tests/BackupRestoreTests.cs`: incremental backup/restore/continue and path-safety coverage; `ServiceTests.cs`, `ProfileStoreTests.cs`, `SnapshotStoreTests.cs`, and `PathDriveServiceTests.cs` cover related service behavior; `OperationServicesTests.cs` covers the coordinator and Output service.

There are empty (0-byte) leftover files still tracked in git: `UniversalBackupTool.Models/Models/*.cs` (root level), `UniversalBackupTool.Services/Interfaces/*.cs`, `UniversalBackupTool.Interfaces/UniversalBackupTool.Interfaces.csproj` and `UniversalBackupTool.Tests/ServiceLayerTests.cs`. `Services/Implementations/*` no longer exists. Legacy `IBackupEngine`/`BackupEngine` is still registered in DI. The UI is wired to `IBackupJobRunner`/`BackupJobRunner`. Do not delete or migrate legacy code without checking references and tests; legacy restore compatibility is intentional.

## Remaining work / recommended order

1. Symlink/reparse point correctness is a functional gap: file enumeration skips symlink files; backup does not resolve them to target bytes. Validate directory symlink traversal, implement safely, and test.
2. User-run F5 validation: first backup; unchanged second run writes zero mod files; one-mod update; metadata touch with equal hash; mod removal; old/new restore; locked files and resume after app restart; ignore patterns; large files; binding errors. For the new Output panel, check:
   - switching tabs during a backup keeps progress;
   - operation buttons are disabled on other pages;
   - Cancel works, and closing the window during an operation is handled;
   - severity colours in light/dark themes;
   - auto-scroll and copy.
3. Structured logging to `Logs/`, including rotation/retention and explicit error reporting. The natural hook is `IOperationOutputService.Write`: mirror entries to a daily log file.
4. Configure and verify self-contained single-file portable publishing for .NET 10.
5. ViewModel tests; ask before adding a ViewModels project reference to Tests.
6. Optional: benchmark and verify Google Drive sync locations and non-NTFS filesystems.

## Implemented versus not yet proven

**Implemented and covered by service tests:** per-mod incremental reuse; changed/removed mod snapshots; metadata-change/content-same hash reuse; restore from shared archives; legacy restore; locked-file retry at the service/job level; ZIP traversal rejection; profile/settings/snapshot storage; vanilla diff and purge safety backup; operation coordinator (single operation, cancel, scope end, phase logging) and Output service (severity, cap, clear). The last reported automated test run was 148 passing NUnit tests on 2026-10-31.

**Not established by those unit tests:** the Output panel and cross-page operation behaviour in the real WPF app, including UI-thread marshalling under a real dispatcher; a real app crash followed by process restart and successful resume; actual locked files under Vortex/antivirus load; real modpack-scale performance and disk-space behavior; WPF binding/visual correctness in all pages; self-contained portable publish; correctness for symlinks/reparse points; or retention cleanup against real backup folders (service coverage: `Backup_MultiComponent_UsesSeparateComponentFoldersAndRetentionPerComponent` and the `Retention_*` tests in `BackupRestoreTests.cs`). Treat these as manual/integration validation or open work, not as already verified.

**Current code-confirmed symlink limitation:** `FileSystemProvider.EnumerateFilesAsync` uses recursive enumeration then excludes files for which `IsSymlink` reports a reparse point. `IsSymlink` currently checks `File.Exists`, catches exceptions and returns false; `ResolveActualPath` exists but is not wired into backup enumeration/archive input. Directory links and junction behavior still need explicit testing. Any fix must avoid recursive traversal loops, escaping the selected source, duplicating targets, and accidentally archiving link metadata instead of target bytes.

## Constraints and implementation notes

- App data is rooted beside the executable via `AppStoragePaths`: settings.json, profiles.json, Logs, Temp/active_backup_job.json, SafetyBackups and Snapshots. Single-file publishing is not yet verified. `%APPDATA%\Vortex` is external Vortex source data, not app storage.
- Use local time (`DateTime.Now`) for user-facing timestamps.
- Do not change namespaces, project files or folder structure unless asked. Preserve unrelated work and existing line endings.
- Every page uses `Style="{StaticResource AppPageStyle}"`, which sets the theme foreground and `ScrollViewer.CanContentScroll="False"`. Pages follow this layout:
  - their own `<ScrollViewer VerticalScrollBarVisibility="Auto">`;
  - inside it, a `Grid` with one `<ColumnDefinition Width="*" MaxWidth="1072" />` (left aligned, capped width);
  - inside that, a `StackPanel Style="{StaticResource PageContentPanelStyle}"`.
- Text must use the `AppStyles.xaml` text styles (`PageTitleTextStyle`, `SectionHeaderTextStyle`, `BodyTextStyle`, `CaptionTextStyle`, `PathTextStyle`, ...) or set `Foreground` from a theme brush. The inherited default foreground is black, which caused unreadable titles in dark mode.
- Nested scrollable lists inside a page: give them a `MaxHeight`, enable virtualization (`CanContentScroll="True"` on the inner ScrollViewer) and set `helpers:NestedScrollBehavior.PassWheelAtEdges="True"`.
- Avoid `ui:Button` in DataTemplates; a previous `PressedForeground` binding error was resolved with plain WPF `Button`. Use `helpers:StackPanelSpacing.Spacing`, not a nonexistent `ui:StackPanel`.
- `BackupJobStateService.SaveAsync` uses `AppContext.BaseDirectory\Temp\active_backup_job.json`; test actual crash/restart resume.
- Legacy `IBackupEngine`/`BackupEngine` remains beside newer `IBackupJobRunner`/`BackupJobRunner`; UI uses the latter. Check references before removing old code.
- Purge deletes files, not empty directories. Safety archives run before purge/restore overwrites.
- For a future code task, first inspect the exact code path and its existing tests, then implement the requested polish/fix; do not use this handoff as a reason to perform a broad audit or unrelated refactor.

## Other Documentation files

- `Basic info.txt`: original rationale, updated with current incremental backup, retry behavior and symlink limitation.
- `Vortex_Backup_Tool_Detailed_Human_Plan.docx`: original human-readable plan, retained as historical design input; this Markdown contains current status.
- `Vortex_Backup_Tool_Mockup.html`: illustrative UI updated with current defaults and labels; it is not the live UI specification.
