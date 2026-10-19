using UniversalBackupTool.Models;

namespace UniversalBackupTool.Interfaces;

public interface ISettingsStore
{
    Task<AppSettings> LoadAsync(string filePath, CancellationToken cancellationToken = default);

    Task SaveAsync(string filePath, AppSettings settings, CancellationToken cancellationToken = default);
}