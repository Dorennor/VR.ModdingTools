namespace UniversalBackupTool.Model.Models.Snapshots
{
    public record SnapshotData(
        Guid ProfileId,
        Dictionary<string, FileMetadata> Files);
}