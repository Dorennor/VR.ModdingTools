using UniversalBackupTool.Model.Models.Backups;
using UniversalBackupTool.Model.Models.Snapshots;

namespace UniversalBackupTool.Interfaces.Snapshots
{
    public interface ISnapshotService
    {
        /// <summary>
        /// Records every entry of the folder, with a SHA-256 and a file version (where it has one) for each regular file.
        /// </summary>
        Task<SnapshotData> CreateSnapshotAsync(
            Guid profileId,
            string rootPath,
            CancellationToken cancellationToken = default,
            IProgress<BackupProgress>? progress = null);

        /// <summary>
        /// New, modified and deleted entries, judged by metadata only (size, times, kind).
        /// </summary>
        Task<Dictionary<string, FileMetadata>> GetChangesAsync(string rootPath, SnapshotData baselineSnapshot, CancellationToken cancellationToken = default);

        /// <summary>
        /// Like <see cref="GetChangesAsync"/>, where matching metadata is only the first pass. With
        /// <paramref name="verifyContent"/> a file that fails it is judged by size, then file version, then hash: it is only
        /// reported as modified when its content differs, wherever the timestamps came from (reinstall, copied or moved game
        /// folder). Files at new paths with vanilla content are listed in <see cref="SnapshotComparison.Relocated"/>.
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
        /// Adds what an existing snapshot lacks (creation time, file version, hash, new timestamps) from the files on disk,
        /// and only for files proven to be the vanilla file: the stored hash matches, or without a stored hash the size and
        /// modified time match. Everything else is counted and left alone, so a modded file never enters the baseline.
        /// <paramref name="onUpgraded"/> receives each new entry at once, so a caller can keep the work when cancelled.
        /// </summary>
        Task<SnapshotUpgradeResult> UpgradeSnapshotAsync(
            string rootPath,
            SnapshotData baselineSnapshot,
            CancellationToken cancellationToken = default,
            IProgress<BackupProgress>? progress = null,
            Action<FileMetadata>? onUpgraded = null);

        /// <summary>
        /// Files at paths the snapshot does not know whose content is that of a vanilla file (moved or copied). Only files
        /// with the size of a vanilla file are read.
        /// </summary>
        Task<IReadOnlyList<RelocatedFile>> FindRelocatedFilesAsync(
            string rootPath,
            SnapshotData baselineSnapshot,
            CancellationToken cancellationToken = default,
            IProgress<BackupProgress>? progress = null);

        /// <summary>
        /// Real folders (not links) of the game folder that are not part of the baseline snapshot, deepest first. Purge
        /// removes them once their unregistered contents are deleted.
        /// </summary>
        Task<IReadOnlyList<string>> GetUnregisteredDirectoriesAsync(string rootPath, SnapshotData baselineSnapshot, CancellationToken cancellationToken = default);

        /// <summary>
        /// Deletes the entries not in the baseline snapshot, then the folders not in it that are empty afterwards. A vanilla
        /// file that was moved (its snapshot path is free) is put back there instead of being deleted.
        /// </summary>
        Task<PurgeResult> PurgeUnregisteredFilesAsync(string rootPath, SnapshotData baselineSnapshot, CancellationToken cancellationToken = default);
    }
}