namespace UniversalBackupTool.Model.Models.Snapshots
{
    public record FileMetadata(
        string RelativePath,
        long SizeBytes,
        DateTime LastWriteTimeUtc,
        bool IsSymlink);
}