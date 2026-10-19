using UniversalBackupTool.Models;

namespace UniversalBackupTool.Interfaces;

public interface IBackupJobStateService
{
    Task SaveAsync(ActiveBackupJob activeBackupJob, CancellationToken cancellationToken = default);

    Task<ActiveBackupJob?> LoadAsync(string filePath, CancellationToken cancellationToken = default);

    Task DeleteAsync(string filePath, CancellationToken cancellationToken = default);
}