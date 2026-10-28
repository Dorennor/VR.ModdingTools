using UniversalBackupTool.Model.Models.Backups;
using UniversalBackupTool.Model.Models.Jobs;
using UniversalBackupTool.Model.Models.Profiles;
using UniversalBackupTool.Model.Models.Settings;

namespace UniversalBackupTool.Interface.Interfaces.Backups
{
    public interface IBackupJobRunner
    {
        /// <summary>
        /// Backs up every profile into its own folder under the configured backup path.
        /// Locked files are recorded in the active job state so the job can be resumed.
        /// </summary>
        Task<BackupBatchResult> RunBackupAsync(
            IReadOnlyList<BackupProfile> profiles,
            BackupOptions options,
            AppSettings settings,
            IProgress<BackupProgress>? progress = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Retries the files that failed in a previous run and appends them to the existing archives.
        /// </summary>
        Task<BackupBatchResult> ResumeBackupAsync(
            ActiveBackupJob job,
            AppSettings settings,
            IProgress<BackupProgress>? progress = null,
            CancellationToken cancellationToken = default);
    }
}