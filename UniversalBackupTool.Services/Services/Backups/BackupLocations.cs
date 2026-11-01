using System.Text.Json;
using System.Text.Json.Serialization;

using UniversalBackupTool.Model.Models.Backups;
using UniversalBackupTool.Model.Models.Profiles;

namespace UniversalBackupTool.Service.Services.Backups
{
    /// <summary>
    /// Where backups live in the backup destination and how their manifests are found, shared by the backup runner and
    /// the tracked-files purge.
    /// </summary>
    public static class BackupLocations
    {
        public static readonly JsonSerializerOptions ManifestJsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };

        /// <summary>
        /// &lt;root&gt;\&lt;game&gt;\&lt;component&gt; for game backups; the shared Vortex AppData backups use
        /// &lt;root&gt;\#Vortex AppData# directly. Each holds the dated snapshot folders and their _archives.
        /// </summary>
        public static string GetComponentDirectory(string backupRoot, BackupProfile profile, BackupComponent component)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(backupRoot);
            ArgumentNullException.ThrowIfNull(profile);

            return VortexAppDataBackup.IsSharedBackup(profile.Id)
                ? Path.Combine(backupRoot, VortexAppDataBackup.FolderName)
                : Path.Combine(backupRoot, SanitizeFileName(profile.GameName), GetComponentFolderName(component));
        }

        public static string GetComponentFolderName(BackupComponent component)
        {
            return component switch
            {
                BackupComponent.StagingFolders => "staging",
                BackupComponent.GameConfigs => "configs",
                BackupComponent.VortexAppData => "appdata",
                BackupComponent.CustomFolders => "custom",
                BackupComponent.GameChanges => "changes",
                BackupComponent.GameAdditions => "additions",
                _ => "misc"
            };
        }

        public static string SanitizeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sanitized = new string(name.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim().TrimEnd('.');

            return sanitized.Length == 0 ? "unnamed" : sanitized;
        }

        /// <summary>
        /// The newest complete backup of the profile in a component folder (matched by profile id, or by game name for a
        /// renamed or recreated profile). Incomplete backups (waiting for Continue Backup) and unreadable manifests are
        /// skipped. Null when there is none.
        /// </summary>
        public static async Task<LocatedBackupManifest?> FindLatestCompleteManifestAsync(
            string componentDirectory,
            BackupProfile profile,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(profile);

            if (!Directory.Exists(componentDirectory))
                return null;

            LocatedBackupManifest? latest = null;

            foreach (var directory in Directory.EnumerateDirectories(componentDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = Path.Combine(directory, BackupManifest.FileName);

                if (!File.Exists(path))
                    continue;

                try
                {
                    await using var stream = File.OpenRead(path);
                    var manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(stream, ManifestJsonOptions, cancellationToken);

                    if (manifest is null || !manifest.IsComplete || manifest.Units is null)
                        continue;

                    var profileMatches = manifest.ProfileId == profile.Id
                        || string.Equals(manifest.GameName, profile.GameName, StringComparison.OrdinalIgnoreCase);

                    if (profileMatches && (latest is null || manifest.CreatedAt > latest.Manifest.CreatedAt))
                        latest = new LocatedBackupManifest(directory, manifest);
                }
                catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
                {
                }
            }

            return latest;
        }
    }
}