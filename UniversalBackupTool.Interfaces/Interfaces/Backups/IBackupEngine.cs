using UniversalBackupTool.Model.Models.Settings;

namespace UniversalBackupTool.Interface.Interfaces.Backups;

public interface IBackupEngine
{
    Task<string> CreateBackupArchiveAsync(
        string sourceRootPath,
        string archivePath,
        AppSettings settings,
        CancellationToken cancellationToken = default);

    Task<string> ResumeBackupArchiveAsync(
        string archivePath,
        string sourceRootPath,
        AppSettings settings,
        CancellationToken cancellationToken = default);
}