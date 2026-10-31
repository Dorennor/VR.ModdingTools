namespace UniversalBackupTool.Model.Models.Backups
{
    public record DirectoryFingerprint(
        string RelativePath,
        long CreationTimeUtcTicks,
        long LastWriteTimeUtcTicks,
        FileAttributes Attributes);
}