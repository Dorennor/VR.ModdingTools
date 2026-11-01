namespace UniversalBackupTool.Model.Models.Snapshots
{
    /// <summary>
    /// Result of comparing a folder with its baseline snapshot.
    /// </summary>
    /// <param name="Changes">New, modified and deleted entries, keyed by relative path.</param>
    /// <param name="Reanchored">
    /// Baseline entries whose timestamp changed but whose content still matches the baseline hash (for example after a
    /// reinstall of the same game version), with the new timestamp. Saving them into the baseline means the next
    /// comparison does not need to hash these files again.
    /// </param>
    /// <param name="UnverifiedFiles">
    /// Files with new timestamps that were reported as modified without a content check because the baseline has no hash
    /// for them (snapshot created before content hashing, or the file could not be read at that time).
    /// </param>
    public sealed record SnapshotComparison(
        Dictionary<string, FileMetadata> Changes,
        IReadOnlyList<FileMetadata> Reanchored,
        int UnverifiedFiles)
    {
        /// <summary>
        /// Files at new paths with the content of a vanilla file. They stay in <see cref="Changes"/>: their new path can
        /// matter (a mod keeping the original under another name), so a backup has to include them.
        /// </summary>
        public IReadOnlyList<RelocatedFile> Relocated { get; init; } = [];

        /// <summary>
        /// Vanilla files whose file version differs from the snapshot: the game was updated or a mod replaced them.
        /// </summary>
        public int VersionChanged { get; init; }

        /// <summary>
        /// Files reported as modified because they needed a content check but could not be read (in use, access denied).
        /// </summary>
        public int UnreadableFiles { get; init; }
    }
}