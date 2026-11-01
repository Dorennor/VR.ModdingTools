namespace UniversalBackupTool.Model.Models.Backups
{
    public record BackupBatchResult(
        List<BackupResult> Results,
        List<FailedBackupFile> FailedFiles,
        int FilesArchived)
    {
        public bool IsComplete => FailedFiles.Count == 0;

        /// <summary>
        /// Batch-level notes that belong to no single result, such as a game skipped because none of the selected
        /// components applies to it.
        /// </summary>
        public List<string> Warnings { get; init; } = [];

        public int ModsArchived => Results.Sum(result => result.ModsArchived);

        public int ModsReused => Results.Sum(result => result.ModsReused);

        public int ModsRemoved => Results.Sum(result => result.ModsRemoved);
    }
}