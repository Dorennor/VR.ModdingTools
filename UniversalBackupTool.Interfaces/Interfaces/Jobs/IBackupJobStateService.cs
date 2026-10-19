using UniversalBackupTool.Model.Models.Jobs;

namespace UniversalBackupTool.Interface.Interfaces.Jobs
{
    public interface IBackupJobStateService
    {
        Task SaveAsync(ActiveBackupJob activeBackupJob, CancellationToken cancellationToken = default);

        Task<ActiveBackupJob?> LoadAsync(string filePath, CancellationToken cancellationToken = default);

        Task DeleteAsync(string filePath, CancellationToken cancellationToken = default);
    }
}