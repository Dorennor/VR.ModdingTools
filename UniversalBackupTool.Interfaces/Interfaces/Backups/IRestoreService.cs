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
        /// The complete backups of a game in the backup destination, newest first: every dated folder with a valid
        /// manifest under &lt;root&gt;\&lt;game&gt;, in a component folder or (older backups) directly. Backups waiting for
        /// Continue Backup, backups with nothing to restore and unreadable manifests are left out. A missing folder gives
        /// an empty list. <paramref name="gameName"/> is a profile's game name or <see cref="VortexAppDataBackup.FolderName"/>.
        /// </summary>
        Task<IReadOnlyList<LocatedBackupManifest>> FindBackupsAsync(string backupRoot, string gameName, CancellationToken cancellationToken = default);

        /// <summary>
        /// Resolves where each part of the backup will be restored, using the current paths of the matching profile.
        /// Vortex AppData parts (the shared backups and older per-game ones) go to <paramref name="vortexAppDataPath"/>,
        /// the folder from the settings, or to the path stored in the backup when it is empty.
        /// </summary>
        IReadOnlyList<RestorePlanItem> BuildPlan(
            BackupManifest manifest,
            IReadOnlyCollection<BackupProfile> profiles,
            IReadOnlyCollection<BackupComponent> components,
            string? vortexAppDataPath = null);

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