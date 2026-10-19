using UniversalBackupTool.Models;

namespace UniversalBackupTool.Interfaces;

public interface ISafetyBackupService
{
    Task<string> CreateSafetyBackupAsync(
        string sourceRootPath,
        string? archiveNamePrefix = null,
        CancellationToken cancellationToken = default);

    Task CleanupExpiredAsync(AppSettings settings, CancellationToken cancellationToken = default);
}