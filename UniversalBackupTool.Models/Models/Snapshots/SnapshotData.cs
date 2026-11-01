namespace UniversalBackupTool.Model.Models.Snapshots
{
    public record SnapshotData(
        Guid ProfileId,
        Dictionary<string, FileMetadata> Files)
    {
        /// <summary>
        /// Relative paths ('/' separated) of the real folders (not links) of the vanilla game folder, including empty ones.
        /// Null in snapshots created before folders were recorded; the folders holding vanilla files are vanilla then.
        /// </summary>
        public List<string>? Directories { get; init; }
    }
}