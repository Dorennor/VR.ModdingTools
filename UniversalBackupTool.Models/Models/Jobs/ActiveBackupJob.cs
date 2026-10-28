using UniversalBackupTool.Model.Models.Backups;

namespace UniversalBackupTool.Model.Models.Jobs
{
    public record ActiveBackupJob(
        Guid JobId,
        Guid ProfileId,
        string ArchivePath,
        string RootPath,
        DateTimeOffset CreatedAtUtc,
        List<string> FailedPaths,
        bool IsCompleted = false)
    {
        /// <summary>
        /// Files that were locked during the backup, with the archive they belong to. Used to resume the job.
        /// </summary>
        public List<FailedBackupFile> FailedFiles { get; init; } = [];

        public static ActiveBackupJob Create(Guid profileId, string archivePath, string rootPath)
            => new(Guid.NewGuid(), profileId, archivePath, rootPath, DateTimeOffset.Now, []);
    }
}