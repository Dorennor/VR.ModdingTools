namespace UniversalBackupTool.Models;

public record ActiveBackupJob(
    Guid JobId,
    Guid ProfileId,
    string ArchivePath,
    string RootPath,
    DateTimeOffset CreatedAtUtc,
    List<string> FailedPaths,
    bool IsCompleted = false)
{
    public static ActiveBackupJob Create(Guid profileId, string archivePath, string rootPath)
        => new(Guid.NewGuid(), profileId, archivePath, rootPath, DateTimeOffset.Now, []);
}