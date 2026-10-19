using UniversalBackupTool.Model.Models.Settings;

namespace UniversalBackupTool.Interface.Interfaces.Backups
{
    public interface ISafetyBackupService
    {
        Task<string> CreateSafetyBackupAsync(
            string sourceRootPath,
            string? archiveNamePrefix = null,
            CancellationToken cancellationToken = default);

        Task CleanupExpiredAsync(AppSettings settings, CancellationToken cancellationToken = default);
    }
}