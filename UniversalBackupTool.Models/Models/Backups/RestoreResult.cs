namespace UniversalBackupTool.Model.Models.Backups
{
    public record RestoreResult(int FilesRestored, string? SafetyBackupPath, List<string> FailedPaths)
    {
        /// <summary>
        /// Files in the backup that were not restored because they match the ignore list (for example Vortex deployment
        /// manifests stored by backups made before the pattern existed).
        /// </summary>
        public int IgnoredFiles { get; init; }
    }
}
