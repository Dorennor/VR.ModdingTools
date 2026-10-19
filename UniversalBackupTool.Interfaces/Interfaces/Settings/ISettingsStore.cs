using UniversalBackupTool.Model.Models.Settings;

namespace UniversalBackupTool.Interface.Interfaces.Settings
{
    public interface ISettingsStore
    {
        Task<AppSettings> LoadAsync(string filePath, CancellationToken cancellationToken = default);

        Task SaveAsync(string filePath, AppSettings settings, CancellationToken cancellationToken = default);
    }
}