namespace UniversalBackupTool.Model.Models.Snapshots
{
    /// <param name="FilesDeleted">Files removed from the game folder.</param>
    /// <param name="SafetyBackupPath">Archive with the deleted files, or null when nothing was deleted.</param>
    public record PurgeResult(int FilesDeleted, string? SafetyBackupPath);
}
