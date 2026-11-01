namespace UniversalBackupTool.Model.Models.Snapshots
{
    /// <param name="FilesDeleted">Entries removed from the game folder, including links.</param>
    /// <param name="SafetyBackupPath">Archive with the deleted files, or null when nothing needed a copy.</param>
    public record PurgeResult(int FilesDeleted, string? SafetyBackupPath)
    {
        /// <summary>
        /// Symbolic links, junctions and hardlinks among <see cref="FilesDeleted"/>. Only the links were removed;
        /// their targets and other hardlink names were not touched.
        /// </summary>
        public int LinksRemoved { get; init; }

        /// <summary>
        /// Folders that are not part of the baseline snapshot and were empty after the files were deleted.
        /// </summary>
        public int FoldersRemoved { get; init; }

        /// <summary>
        /// Folders not part of the baseline snapshot that were kept because they still contain something (a file that
        /// could not be deleted) or could not be removed (in use, access denied).
        /// </summary>
        public int FoldersKept { get; init; }
    }
}
