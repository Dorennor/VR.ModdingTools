namespace UniversalBackupTool.Model.Models.Backups
{
    public record BackupBatchResult(
        List<BackupResult> Results,
        List<FailedBackupFile> FailedFiles,
        int FilesArchived)
    {
        public bool IsComplete => FailedFiles.Count == 0;
    }
}