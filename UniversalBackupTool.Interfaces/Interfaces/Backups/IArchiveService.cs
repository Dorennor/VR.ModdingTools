using UniversalBackupTool.Model.Models.Backups;
using UniversalBackupTool.Model.Models.Settings;

namespace UniversalBackupTool.Interface.Interfaces.Backups
{
    public interface IArchiveService
    {
        /// <summary>
        /// Creates a new zip archive. Files that are locked or access-denied are skipped and returned in the result.
        /// </summary>
        Task<ArchiveWriteResult> CreateArchiveAsync(
            string archivePath,
            IReadOnlyList<ArchiveFileItem> files,
            AppSettings settings,
            IProgress<ArchiveProgress>? progress = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Adds files to an existing archive (created when missing). Entries with the same name are replaced.
        /// </summary>
        Task<ArchiveWriteResult> AppendToArchiveAsync(
            string archivePath,
            IReadOnlyList<ArchiveFileItem> files,
            AppSettings settings,
            IProgress<ArchiveProgress>? progress = null,
            CancellationToken cancellationToken = default);
    }
}