namespace UniversalBackupTool.Model.Models.Backups
{
    public record BackupResult(
        string GameName,
        string BackupDirectory,
        int FilesArchived,
        List<string> Warnings,
        List<FailedBackupFile> FailedFiles);
}