using System.Text.Json;

using UniversalBackupTool.Interface.Interfaces.Profiles;
using UniversalBackupTool.Model.Models.Profiles;

namespace UniversalBackupTool.Service.Services.Profiles
{
    public sealed class JsonProfileStore : IProfileStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        public async Task<IReadOnlyList<BackupProfile>> LoadAsync(string filePath, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("Profiles file path cannot be empty.", nameof(filePath));

            if (!File.Exists(filePath))
                return [];

            await using var stream = File.OpenRead(filePath);
            var profiles = await JsonSerializer.DeserializeAsync<List<BackupProfile>>(stream, JsonOptions, cancellationToken);

            return profiles ?? [];
        }

        public async Task SaveAsync(string filePath, IReadOnlyCollection<BackupProfile> profiles, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("Profiles file path cannot be empty.", nameof(filePath));

            ArgumentNullException.ThrowIfNull(profiles);

            var directory = Path.GetDirectoryName(filePath);

            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            // Write to a temp file first so a crash never leaves a half-written profiles.json.
            var tempPath = filePath + ".tmp";

            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, profiles, JsonOptions, cancellationToken);
            }

            File.Move(tempPath, filePath, overwrite: true);
        }
    }
}