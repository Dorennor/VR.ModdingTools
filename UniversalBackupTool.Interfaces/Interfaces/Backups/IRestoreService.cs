using UniversalBackupTool.Model.Models.Backups;
using UniversalBackupTool.Model.Models.Profiles;

namespace UniversalBackupTool.Interface.Interfaces.Backups
{
    public interface IRestoreService
    {
        /// <summary>
        /// Reads the manifest of a backup folder. Returns null when it is missing or invalid.
        /// </summary>
        Task<BackupManifest?> ReadManifestAsync(string backupDirectory, CancellationToken cancellationToken = default);

        /// <summary>
        /// Resolves where each part of the backup will be restored, using the current paths of the matching profile.
        /// </summary>
        IReadOnlyList<RestorePlanItem> BuildPlan(
            BackupManifest manifest,
            IReadOnlyCollection<BackupProfile> profiles,
            IReadOnlyCollection<BackupComponent> components);

        /// <summary>
        /// Saves the files that would be overwritten into a safety backup, then extracts the plan.
        /// </summary>
        Task<RestoreResult> RestoreAsync(
            string backupDirectory,
            IReadOnlyList<RestorePlanItem> plan,
            IProgress<BackupProgress>? progress = null,
            CancellationToken cancellationToken = default);
    }
}