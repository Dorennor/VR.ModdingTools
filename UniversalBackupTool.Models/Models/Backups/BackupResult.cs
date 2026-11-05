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

        /// <summary>
        /// The archive of a full game backup; null for every other component.
        /// </summary>
        public string? FullGameArchivePath { get; init; }

        public int ModsArchived { get; init; }

        public int ModsReused { get; init; }

        public int ModsRemoved { get; init; }

        public List<PendingArchiveMove> PendingArchiveMoves { get; init; } = [];

        /// <summary>
        /// Files skipped because they match the global ignore list (when the profile uses it).
        /// </summary>
        public int IgnoredFiles { get; init; }

        /// <summary>
        /// A few of the ignored paths (relative to their component root), to show in Output.
        /// </summary>
        public List<string> IgnoredExamples { get; init; } = [];
    }
}