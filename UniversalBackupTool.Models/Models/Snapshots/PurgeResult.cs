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
    }
}
