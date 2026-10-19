using System.Text.Json;

using UniversalBackupTool.Interface.Interfaces.Settings;
using UniversalBackupTool.Model.Models.Settings;

namespace UniversalBackupTool.Service.Services.Settings
{
    public sealed class JsonSettingsStore : ISettingsStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        public async Task<AppSettings> LoadAsync(string filePath, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("Settings file path cannot be empty.", nameof(filePath));

            if (!File.Exists(filePath))
            {
                var defaultSettings = AppSettings.Default(Path.Combine(AppContext.BaseDirectory, "Backups"));
                await SaveAsync(filePath, defaultSettings, cancellationToken);
                return defaultSettings;
            }

            await using var stream = File.OpenRead(filePath);
            var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, cancellationToken);

            if (settings is null)
            {
                var fallbackSettings = AppSettings.Default(Path.Combine(AppContext.BaseDirectory, "Backups"));
                await SaveAsync(filePath, fallbackSettings, cancellationToken);
                return fallbackSettings;
            }

            return settings;
        }

        public async Task SaveAsync(string filePath, AppSettings settings, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("Settings file path cannot be empty.", nameof(filePath));

            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using var stream = File.Create(filePath);
            await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken);
        }
    }
}