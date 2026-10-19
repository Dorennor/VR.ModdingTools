using System.Text.Json;

using UniversalBackupTool.Interface.Interfaces.Jobs;
using UniversalBackupTool.Model.Models.Jobs;

namespace UniversalBackupTool.Service.Services.Jobs
{
    public sealed class BackupJobStateService : IBackupJobStateService
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

        public async Task SaveAsync(ActiveBackupJob activeBackupJob, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(activeBackupJob);

            var filePath = Path.Combine(AppContext.BaseDirectory, "Temp", "active_backup_job.json");
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            await using var stream = File.Create(filePath);
            await JsonSerializer.SerializeAsync(stream, activeBackupJob, JsonOptions, cancellationToken);
        }

        public async Task<ActiveBackupJob?> LoadAsync(string filePath, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return null;

            await using var stream = File.OpenRead(filePath);
            return await JsonSerializer.DeserializeAsync<ActiveBackupJob>(stream, JsonOptions, cancellationToken);
        }

        public Task DeleteAsync(string filePath, CancellationToken cancellationToken = default)
        {
            if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
                File.Delete(filePath);

            return Task.CompletedTask;
        }
    }
}