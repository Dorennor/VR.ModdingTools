namespace UniversalBackupTool.Model.Models.Backups
{
    /// <param name="FileCompleted">True once per file, including files that were skipped or failed.</param>
    public record ArchiveProgress(string CurrentFile, long BytesDelta, bool FileCompleted);
}