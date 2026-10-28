namespace UniversalBackupTool.Model.Models.Backups
{
    public record FailedBackupFile(string ArchivePath, string EntryName, string SourcePath);
}