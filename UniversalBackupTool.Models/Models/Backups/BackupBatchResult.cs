namespace UniversalBackupTool.Model.Models.Backups
{
    public record BackupBatchResult(
        List<BackupResult> Results,
        List<FailedBackupFile> FailedFiles,
        int FilesArchived)
    {
        public bool IsComplete => FailedFiles.Count == 0;

        public int ModsArchived => Results.Sum(result => result.ModsArchived);

        public int ModsReused => Results.Sum(result => result.ModsReused);

        public int ModsRemoved => Results.Sum(result => result.ModsRemoved);
    }
}