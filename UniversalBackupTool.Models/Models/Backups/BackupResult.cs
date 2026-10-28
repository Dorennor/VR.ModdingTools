namespace UniversalBackupTool.Model.Models.Backups
{
    public record BackupResult(
        string GameName,
        string BackupDirectory,
        int FilesArchived,
        List<string> Warnings,
        List<FailedBackupFile> FailedFiles)
    {
        public string? ManifestPath { get; init; }

        public int ModsArchived { get; init; }

        public int ModsReused { get; init; }

        public int ModsRemoved { get; init; }

        public List<PendingArchiveMove> PendingArchiveMoves { get; init; } = [];
    }
}