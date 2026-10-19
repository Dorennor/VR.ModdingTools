namespace UniversalBackupTool.Models;

public record SnapshotData(
    Guid ProfileId,
    Dictionary<string, FileMetadata> Files);