namespace UniversalBackupTool.Model.Models.Backups
{
    /// <param name="FilesWritten">Files successfully stored in the archive.</param>
    /// <param name="Failed">Files that could not be read (locked or access denied) and must be retried later.</param>
    public record ArchiveWriteResult(int FilesWritten, List<ArchiveFileItem> Failed)
    {
        public Dictionary<string, string> FileHashes { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }
}