namespace UniversalBackupTool.Model.Models.Snapshots
{
    public record StoredSnapshot(
        SnapshotData Data,
        DateTime CreatedAt);
}