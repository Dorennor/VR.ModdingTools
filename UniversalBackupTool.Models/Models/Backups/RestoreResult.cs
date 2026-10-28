namespace UniversalBackupTool.Model.Models.Backups
{
    public record RestoreResult(int FilesRestored, string? SafetyBackupPath, List<string> FailedPaths);
}