using System.Text.Json;

using UniversalBackupTool.Interface.Interfaces.Jobs;
using UniversalBackupTool.Interfaces.Storage;
using UniversalBackupTool.Model.Models.Jobs;

namespace UniversalBackupTool.Service.Services.Jobs
{
    public sealed class BackupJobStateService : IBackupJobStateService
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

        private readonly string _jobFilePath;

        /// <summary>
        /// Uses the default application folder (AppContext.BaseDirectory\Temp).
        /// </summary>
        public BackupJobStateService()
        {
            _jobFilePath = Path.Combine(AppContext.BaseDirectory, "Temp", "active_backup_job.json");
        }

        /// <summary>
        /// Saves to the same file the application loads from (<c>AppStoragePaths.ActiveBackupJobFilePath</c>).
        /// </summary>
        public BackupJobStateService(IAppStoragePathsProvider storagePathsProvider)
        {
            ArgumentNullException.ThrowIfNull(storagePathsProvider);
            _jobFilePath = storagePathsProvider.GetPaths().ActiveBackupJobFilePath;
        }

        public async Task SaveAsync(ActiveBackupJob activeBackupJob, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(activeBackupJob);

            var directory = Path.GetDirectoryName(_jobFilePath);

            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            // Write then replace, so a crash during the write never leaves a half-written job that Continue Backup needs.
            var temporaryPath = _jobFilePath + ".tmp";

            await using (var stream = File.Create(temporaryPath))
                await JsonSerializer.SerializeAsync(stream, activeBackupJob, JsonOptions, cancellationToken);

            File.Move(temporaryPath, _jobFilePath, overwrite: true);
        }

        public async Task<ActiveBackupJob?> LoadAsync(string filePath, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return null;

            try
            {
                await using var stream = File.OpenRead(filePath);

                return await JsonSerializer.DeserializeAsync<ActiveBackupJob>(stream, JsonOptions, cancellationToken);
            }
            catch (JsonException)
            {
                // An unreadable job file is treated as "no paused job" instead of breaking the Backup page.
                return null;
            }
        }

        public Task DeleteAsync(string filePath, CancellationToken cancellationToken = default)
        {
            if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
                File.Delete(filePath);

            return Task.CompletedTask;
        }
    }
}
