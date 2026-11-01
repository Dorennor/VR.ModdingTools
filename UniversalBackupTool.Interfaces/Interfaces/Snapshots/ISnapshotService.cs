using UniversalBackupTool.Model.Models.Backups;
using UniversalBackupTool.Model.Models.Snapshots;

namespace UniversalBackupTool.Interfaces.Snapshots
{
    public interface ISnapshotService
    {
        /// <summary>
        /// Records every entry of the folder, with a SHA-256 for each regular file.
        /// </summary>
        Task<SnapshotData> CreateSnapshotAsync(
            Guid profileId,
            string rootPath,
            CancellationToken cancellationToken = default,
            IProgress<BackupProgress>? progress = null);

        /// <summary>
        /// New, modified and deleted entries, judged by metadata only (size, time, kind).
        /// </summary>
        Task<Dictionary<string, FileMetadata>> GetChangesAsync(string rootPath, SnapshotData baselineSnapshot, CancellationToken cancellationToken = default);

        /// <summary>
        /// Like <see cref="GetChangesAsync"/>; with <paramref name="verifyContent"/> a file whose size and kind match the
        /// baseline but whose timestamp differs is hashed, and it is only reported as modified when its content differs.
        /// <paramref name="onReanchored"/> receives each file found identical as soon as it is hashed, so a caller can keep
        /// that work even when the comparison is cancelled.
        /// </summary>
        Task<SnapshotComparison> CompareAsync(
            string rootPath,
            SnapshotData baselineSnapshot,
            bool verifyContent,
            CancellationToken cancellationToken = default,
            IProgress<BackupProgress>? progress = null,
            Action<FileMetadata>? onReanchored = null);

        /// <summary>
        /// Real folders (not links) of the game folder that are not part of the baseline snapshot, deepest first. Purge
        /// removes them once their unregistered contents are deleted.
        /// </summary>
        Task<IReadOnlyList<string>> GetUnregisteredDirectoriesAsync(string rootPath, SnapshotData baselineSnapshot, CancellationToken cancellationToken = default);

        /// <summary>
        /// Deletes the entries not in the baseline snapshot, then the folders not in it that are empty afterwards.
        /// </summary>
        Task<PurgeResult> PurgeUnregisteredFilesAsync(string rootPath, SnapshotData baselineSnapshot, CancellationToken cancellationToken = default);
    }
}