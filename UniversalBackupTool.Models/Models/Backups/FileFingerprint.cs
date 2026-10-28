namespace UniversalBackupTool.Model.Models.Backups
{
    public record FileFingerprint(
        string RelativePath,
        long Length,
        long CreationTimeUtcTicks,
        long LastWriteTimeUtcTicks,
        FileAttributes Attributes,
        string Sha256);
}
