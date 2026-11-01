using UniversalBackupTool.Model.Models.Backups;

namespace UniversalBackupTool.Model.Models.Snapshots
{
    /// <summary>
    /// What the tracked-files purge does with one file listed in the game's backups.
    /// </summary>
    public enum TrackedFileStatus
    {
        /// <summary>
        /// Still exactly the file from the backup (same SHA-256): it is ours and is removed.
        /// </summary>
        Remove,

        /// <summary>
        /// Its content differs from the backup (most likely a game update replaced it): kept.
        /// </summary>
        ChangedSinceBackup,

        /// <summary>
        /// Part of the vanilla snapshot: an original game file a mod had overwritten. Kept; a game update or a file
        /// verification restores it.
        /// </summary>
        OriginalGameFile,

        /// <summary>
        /// A hardlink, symlink or folder link: a Vortex deployment. Kept; Vortex's own Purge removes it.
        /// </summary>
        VortexLink,

        /// <summary>
        /// The backup has no content hash for it, or it cannot be read now (in use, access denied): kept.
        /// </summary>
        CannotVerify
    }

    /// <param name="RelativePath">Path relative to the game folder, '/' separated.</param>
    /// <param name="Length">Size when the plan was made; the purge skips a file whose size or time changed since.</param>
    /// <param name="LastWriteTimeUtc">Last write time when the plan was made.</param>
    public sealed record TrackedFile(string RelativePath, TrackedFileStatus Status, long Length, DateTime LastWriteTimeUtc);

    /// <summary>
    /// The files of the newest complete Changed game files and Game configs backups that are still in the game folder,
    /// each with what the purge does with it, and the folders it removes when they end up empty.
    /// </summary>
    /// <param name="RootPath">The game folder.</param>
    /// <param name="Backups">The backups the files were taken from (newest complete Changed game files and Game configs).</param>
    /// <param name="Files">Tracked files that exist in the game folder, sorted by path.</param>
    /// <param name="Folders">Folders recorded in the backups that are not in the vanilla snapshot, deepest first; removed when empty.</param>
    /// <param name="FilesAlreadyGone">Tracked files that no longer exist in the game folder.</param>
    public sealed record TrackedPurgePlan(
        string RootPath,
        IReadOnlyList<LocatedBackupManifest> Backups,
        IReadOnlyList<TrackedFile> Files,
        IReadOnlyList<string> Folders,
        int FilesAlreadyGone)
    {
        public IEnumerable<TrackedFile> ToRemove => Files.Where(file => file.Status == TrackedFileStatus.Remove);

        public int Count(TrackedFileStatus status) => Files.Count(file => file.Status == status);

        public bool HasBackups => Backups.Count > 0;
    }

    /// <param name="FilesDeleted">Files removed from the game folder.</param>
    /// <param name="SafetyBackupPath">Archive with the removed files, or null when nothing was removed.</param>
    /// <param name="FoldersRemoved">Folders that were empty afterwards and were removed.</param>
    /// <param name="FoldersKept">Folders that still hold something (or could not be removed) and were kept.</param>
    /// <param name="FilesKept">Files planned for removal that were kept: changed since the preview, in use or access denied.</param>
    public sealed record TrackedPurgeResult(
        TrackedPurgePlan Plan,
        int FilesDeleted,
        string? SafetyBackupPath,
        int FoldersRemoved,
        int FoldersKept,
        IReadOnlyList<string> FilesKept);
}