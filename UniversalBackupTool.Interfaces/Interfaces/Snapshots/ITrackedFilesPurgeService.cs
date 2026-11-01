using UniversalBackupTool.Model.Models.Backups;
using UniversalBackupTool.Model.Models.Profiles;
using UniversalBackupTool.Model.Models.Snapshots;

namespace UniversalBackupTool.Interfaces.Snapshots
{
    /// <summary>
    /// Purge for a game folder whose vanilla snapshot no longer matches (the game was updated): removes the files the
    /// tool backed up (newest complete Changed game files and Game configs backups) instead of everything outside the
    /// snapshot. A file is removed only while its content still matches the backup; original game files, links (Vortex
    /// deployments, purged in Vortex) and files that cannot be verified are kept.
    /// </summary>
    public interface ITrackedFilesPurgeService
    {
        /// <param name="profile">The game; its backups are looked up by id, or by name for a recreated profile.</param>
        /// <param name="rootPath">The game folder.</param>
        /// <param name="baselineSnapshot">The vanilla snapshot, which tells original game files and folders apart.</param>
        /// <param name="backupRoot">The backup destination from the settings.</param>
        Task<TrackedPurgePlan> BuildPlanAsync(
            BackupProfile profile,
            string rootPath,
            SnapshotData baselineSnapshot,
            string backupRoot,
            IProgress<BackupProgress>? progress = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Removes the plan's files whose size and time are still what the plan saw (a safety backup of them is made
        /// first), then the plan's folders that are empty afterwards.
        /// </summary>
        Task<TrackedPurgeResult> PurgeAsync(TrackedPurgePlan plan, CancellationToken cancellationToken = default);
    }
}