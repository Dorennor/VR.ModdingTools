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
        /// Game configs entries matching <paramref name="ignorePatterns"/> (relative to the game folder, like at backup time)
        /// are not restored, so files ignored today are not brought back from older backups. Other components are always
        /// restored completely.
        /// </summary>
        Task<RestoreResult> RestoreAsync(
            string backupDirectory,
            IReadOnlyList<RestorePlanItem> plan,
            IProgress<BackupProgress>? progress = null,
            CancellationToken cancellationToken = default,
            IReadOnlyCollection<string>? ignorePatterns = null);
    }
}