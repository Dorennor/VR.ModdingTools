# Version History

The version lives in one place: `<Version>` in `Directory.Build.props`. The Output panel shows it at startup ("Vortex Backup Tool X.Y.Z started"), and so do the .exe file properties.

## Rules

Format: `0.MINOR.PATCH`. The major number stays 0 until you decide the tool is "1.0".

| Change | Bump | Example |
|---|---|---|
| Any commit: formatting, fixes, documentation, refactoring, tests, small UI tweaks | +1 patch | 0.19.1 → 0.19.2 |
| A new feature, a new backup component, or a major rework/rewrite | +1 minor, patch back to 0 | 0.19.4 → 0.20.0 |

- **Patch bumps are automatic.** `.githooks/pre-commit` raises the patch number in `Directory.Build.props` and adds it to the commit. Enable it once per clone:
  `git config core.hooksPath .githooks`
- **Minor bumps are manual.** In the commit that brings the big change, set `<Version>` to the next minor version yourself and add a row to the **Minor versions** table below. The hook sees that the commit already changes the version and leaves it alone.
- `git commit --amend` runs the hook again and would bump once more. Use `git commit --amend --no-verify` when amending.
- To find the commit of any later version: `git log -L '/<Version>/,+1:Directory.Build.props'`.

## Minor versions

| Version | Date | Change |
|---|---|---|
| 0.1.0 | 2026-08-01 | First version of the tool (project files). |
| 0.2.0 | 2026-10-20 | Rewrite ("Init.") of the proof of concept. |
| 0.3.0 | 2026-10-28 | Main implementation. |
| 0.4.0 | 2026-10-31 | Large refactoring. |
| 0.5.0 | 2026-10-31 | UI rework: Windows 11 / Visual Studio 2026 style. |
| 0.6.0 | 2026-10-31 | Links, custom paths, log files, portable publish, ViewModel tests. |
| 0.7.0 | 2026-11-01 | Game configs (new files only) separated from Changed game files. |
| 0.8.0 | 2026-11-01 | Content hashes in snapshots, empty folder cleanup on purge. |
| 0.9.0 | 2026-11-01 | App icon, Install/Uninstall scripts. |
| 0.10.0 | 2026-11-01 | Profiles without Vortex. |
| 0.11.0 | 2026-11-01 | Shared Vortex AppData backup, global AppData folder setting. |
| 0.12.0 | 2026-11-01 | Purge tracked files after a game update. |
| 0.13.0 | 2026-11-01 | Faster backups: parallel archiving, fewer compressed files. |
| 0.14.0 | 2026-11-01 | Fast "New game files" backup component. |
| 0.15.0 | 2026-11-01 | Reliable snapshots (version, size, hash), moved files, snapshot upgrade. |
| 0.16.0 | 2026-11-02 | "Show only installed games" filter. |
| 0.17.0 | 2026-11-05 | Full game backup with a per-profile export folder. |
| 0.18.0 | 2026-11-08 | Dashboard compact view. |
| 0.19.0 | 2026-11-09 | Layout adapts to window size, proportions and maximized mode. |

## Backfilled history (before 2026-11-11)

Versioning started on 2026-11-11, so these versions were calculated afterwards with the rules above. Before that every build reported 1.0.0, the .NET default. The first commit only added git files and has no version. Minor versions are in bold.

| Version | Date | Commit | Message |
|---|---|---|---|
| — | 2026-08-01 | `518c0e4` | Add .gitattributes and .gitignore. |
| **0.1.0** | 2026-08-01 | `553eae7` | Add project files. |
| 0.1.1 | 2026-08-01 | `da239d7` | Improvements. |
| 0.1.2 | 2026-08-01 | `acd8137` | Small UI changes. |
| 0.1.3 | 2026-08-01 | `2a90eb3` | Preserved publish. |
| 0.1.4 | 2026-08-01 | `49e5d76` | Fixes. |
| 0.1.5 | 2026-08-01 | `bca4a5b` | Fix for .bat. |
| 0.1.6 | 2026-08-02 | `c6c7f99` | Ignore for some paths. |
| **0.2.0** | 2026-10-20 | `51196f5` | Init. |
| 0.2.1 | 2026-10-20 | `940eb66` | Fixes. |
| 0.2.2 | 2026-10-20 | `13469cd` | Fixes. |
| 0.2.3 | 2026-10-20 | `d00b384` | Tests. |
| 0.2.4 | 2026-10-20 | `37d4fd1` | Tests. |
| 0.2.5 | 2026-10-20 | `3041dc2` | Temp. |
| **0.3.0** | 2026-10-28 | `23a3f4b` | Implementation. |
| 0.3.1 | 2026-10-28 | `f6f613e` | Fix. |
| 0.3.2 | 2026-10-28 | `df0b610` | Refactoring. |
| 0.3.3 | 2026-10-31 | `bb341bb` | Documentation. |
| 0.3.4 | 2026-10-31 | `05427bf` | Removed unused stuff. |
| 0.3.5 | 2026-10-31 | `7a01704` | Refactoring. |
| 0.3.6 | 2026-10-31 | `f5771d0` | Refactoring. |
| **0.4.0** | 2026-10-31 | `979a23a` | Refactoring. |
| 0.4.1 | 2026-10-31 | `6ee6729` | Purge preview: show all files in a virtualized, scrollable list. |
| **0.5.0** | 2026-10-31 | `7befa70` | UI rework: Windows 11 / Visual Studio 2026 style. |
| **0.6.0** | 2026-10-31 | `88adf18` | Links, custom paths, log files, portable publish, fixes and ViewModel tests. |
| 0.6.1 | 2026-10-31 | `0a101f4` | Ignore list filters only Game configs; Vortex files ignored by default. |
| **0.7.0** | 2026-11-01 | `509030f` | Separate Game configs (new files only) from Changed game files. |
| **0.8.0** | 2026-11-01 | `f9cdde6` | Content hashes in snapshots and empty folder cleanup on purge. |
| 0.8.1 | 2026-11-01 | `d2ef22d` | Status bar: keep file and size counters visible. |
| 0.8.2 | 2026-11-01 | `4cf1e3e` | Sorted profile lists, kept backup toggles, full changed game files. |
| 0.8.3 | 2026-11-01 | `59a86b1` | Formatting, |
| 0.8.4 | 2026-11-01 | `e44fa37` | Documentation: changed game files, snapshot hashes, folder purge, UI changes. |
| **0.9.0** | 2026-11-01 | `2c87470` | App icon, Install/Uninstall scripts and documentation. |
| **0.10.0** | 2026-11-01 | `5a9b4af` | Profiles without Vortex. |
| 0.10.1 | 2026-11-01 | `263c4de` | Documentation: neutral, consistent file names. |
| 0.10.2 | 2026-11-01 | `8d71595` | Remove empty leftover files and the unused legacy BackupEngine. |
| 0.10.3 | 2026-11-01 | `fa6f8ae` | Formatting. |
| **0.11.0** | 2026-11-01 | `7609855` | Shared Vortex AppData backup and a global AppData folder setting. |
| **0.12.0** | 2026-11-01 | `4048b27` | Purge tracked files: remove backed-up files after a game update. |
| 0.12.1 | 2026-11-01 | `257bfee` | Formatting. |
| **0.13.0** | 2026-11-01 | `81de5f4` | Faster backups: parallel archiving and fewer compressed files. |
| 0.13.1 | 2026-11-01 | `8d7adce` | Formatting. |
| 0.13.2 | 2026-11-01 | `5156bec` | Backup picker: separate Vortex games and other games. |
| 0.13.3 | 2026-11-01 | `ca921dc` | Formatting. |
| 0.13.4 | 2026-11-01 | `e6c1ae2` | Formatting. |
| 0.13.5 | 2026-11-01 | `4fb522e` | Install script works from the publish output and build folders. |
| **0.14.0** | 2026-11-01 | `f318895` | Backup: fast "New game files" component; keep hashing progress on cancel. |
| 0.14.1 | 2026-11-01 | `b591c01` | Formatting. |
| **0.15.0** | 2026-11-01 | `e145721` | Snapshots: version, size and hash decide; detect moved files; upgrade existing snapshots. |
| 0.15.1 | 2026-11-01 | `62728e4` | Formatting. |
| 0.15.2 | 2026-11-02 | `ecd91e3` | Backup: count symlinks at their target's size in progress totals. |
| **0.16.0** | 2026-11-02 | `a590383` | Backup: optional "Show only installed games" filter for the profile picker. |
| 0.16.1 | 2026-11-02 | `a2f26b9` | Formatting. |
| 0.16.2 | 2026-11-02 | `ac24192` | Formatting. |
| 0.16.3 | 2026-11-05 | `84ca38c` | UI: replace the system message box with a styled confirmation dialog. |
| 0.16.4 | 2026-11-05 | `6c1b303` | Logs: use the .log extension and rename existing .txt logs on first write. |
| 0.16.5 | 2026-11-05 | `1be7fce` | Formatting. |
| **0.17.0** | 2026-11-05 | `6ffd9b0` | Backup: add a full game backup with a per-profile export folder. |
| 0.17.1 | 2026-11-05 | `836091e` | Formatting. |
| 0.17.2 | 2026-11-05 | `0f2a1bd` | Formatting. |
| 0.17.3 | 2026-11-07 | `cba0ebe` | Dashboard: scroll to the profile editor when it opens; close right away if the operation ended during the exit prompt. |
| **0.18.0** | 2026-11-08 | `6fca7f2` | Dashboard: add a compact view that shows each profile as a single row. |
| 0.18.1 | 2026-11-09 | `85bd3f5` | Backup: keep the game list scrollbar off the Select all buttons. |
| **0.19.0** | 2026-11-09 | `5f757ee` | UI: adapt the layout to window size, proportions and maximized mode. |
| 0.19.1 | 2026-11-09 | `a4dd739` | Formatting. |
| 0.19.2 | 2026-11-11 | — | Versioning: version in Directory.Build.props, automatic patch bump, this history. |
