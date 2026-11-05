namespace UniversalBackupTool.Model.Models.Backups
{
    /// <param name="SourcePath">The file to read.</param>
    /// <param name="EntryName">Path inside the archive. A name ending in '/' stores an empty folder and reads nothing.</param>
    public record ArchiveFileItem(string SourcePath, string EntryName)
    {
        public bool IsEmptyFolder => EntryName.EndsWith('/');
    }
}
