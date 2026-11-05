using System.Text.Json;

using UniversalBackupTool.Interface.Interfaces.Settings;
using UniversalBackupTool.Model.Models.Settings;
using UniversalBackupTool.Model.Models.Storage;

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

            AppSettings? settings;

            // Close the file before anything below may save it again (fallback defaults, migration).
            await using (var stream = File.OpenRead(filePath))
                settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, cancellationToken);

            if (settings is null)
            {
                var fallbackSettings = AppSettings.Default(Path.Combine(AppContext.BaseDirectory, "Backups"));
                await SaveAsync(filePath, fallbackSettings, cancellationToken);

                return fallbackSettings;
            }

            var baseDirectory = GetBaseDirectory(filePath);

            settings = settings with
            {
                GoogleDriveBackupPath = ResolvePortablePath(settings.GoogleDriveBackupPath, baseDirectory),
                SafetyBackupPath = ResolvePortablePath(settings.SafetyBackupPath, baseDirectory)
            };

            if (settings.DefaultsVersion < AppSettings.CurrentDefaultsVersion)
            {
                if (settings.DefaultsVersion < AppSettings.GlobalVortexAppDataVersion && string.IsNullOrWhiteSpace(settings.VortexAppDataPath))
                    settings = settings with { VortexAppDataPath = await FindProfileVortexAppDataPathAsync(baseDirectory, cancellationToken) ?? AppSettings.DefaultVortexAppDataPath };

                // Parallel archiving starts at the number of CPU cores of this PC.
                if (settings.DefaultsVersion < AppSettings.ParallelArchivesVersion && settings.ParallelArchives < 1)
                    settings = settings with { ParallelArchives = AppSettings.DefaultParallelArchives };

                if (settings.DefaultsVersion < AppSettings.ShowOnlyInstalledGamesVersion)
                    settings = settings with { ShowOnlyInstalledGames = true };

                settings = AddNewDefaults(settings);
                await SaveAsync(filePath, settings, cancellationToken);
            }

            return settings;
        }

        /// <summary>
        /// Before defaults version 3 every Vortex profile in profiles.json (next to settings.json) had its own
        /// VortexAppDataPath. The first one becomes the global setting; the profiles file itself is left as it is.
        /// </summary>
        private static async Task<string?> FindProfileVortexAppDataPathAsync(string baseDirectory, CancellationToken cancellationToken)
        {
            var profilesPath = new AppStoragePaths(baseDirectory).ProfilesFilePath;

            if (!File.Exists(profilesPath))
                return null;

            try
            {
                await using var stream = File.OpenRead(profilesPath);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

                if (document.RootElement.ValueKind != JsonValueKind.Array)
                    return null;

                foreach (var profile in document.RootElement.EnumerateArray())
                {
                    if (profile.ValueKind != JsonValueKind.Object)
                        continue;

                    // Profiles saved before the UsesVortex setting existed are Vortex profiles.
                    var usesVortex = !TryGetProperty(profile, "UsesVortex", out var usesVortexValue) || usesVortexValue.ValueKind != JsonValueKind.False;

                    if (usesVortex
                        && TryGetProperty(profile, "VortexAppDataPath", out var path)
                        && path.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(path.GetString()))
                        return path.GetString()!.Trim();
                }
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
            {
            }

            return null;
        }

        private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }

            value = default;
            return false;
        }

        /// <summary>
        /// Gives settings files written by older versions the default ignore patterns once, keeping the user's own patterns.
        /// </summary>
        private static AppSettings AddNewDefaults(AppSettings settings)
        {
            var patterns = settings.GlobalIgnorePatterns.ToList();
            var newDefaults = AppSettings.VersionedDefaultIgnorePatterns
                .Where(item => item.SinceVersion > settings.DefaultsVersion)
                .Select(item => item.Pattern);

            foreach (var pattern in newDefaults)
            {
                if (!patterns.Contains(pattern, StringComparer.OrdinalIgnoreCase))
                    patterns.Add(pattern);
            }

            var extensions = settings.NoCompressionExtensions.ToList();
            var newExtensions = AppSettings.VersionedDefaultNoCompressionExtensions
                .Where(item => item.SinceVersion > settings.DefaultsVersion)
                .Select(item => item.Extension);

            foreach (var extension in newExtensions)
            {
                if (!extensions.Any(existing => string.Equals(AppSettings.NormalizeExtension(existing), extension, StringComparison.OrdinalIgnoreCase)))
                    extensions.Add(extension);
            }

            return settings with
            {
                GlobalIgnorePatterns = patterns,
                NoCompressionExtensions = extensions,
                DefaultsVersion = AppSettings.CurrentDefaultsVersion
            };
        }

        public async Task SaveAsync(string filePath, AppSettings settings, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("Settings file path cannot be empty.", nameof(filePath));

            ArgumentNullException.ThrowIfNull(settings);

            var directory = Path.GetDirectoryName(filePath);

            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            // Folders inside the application folder are stored relative to it, so the portable folder can be moved.
            var baseDirectory = GetBaseDirectory(filePath);
            var portable = settings with
            {
                GoogleDriveBackupPath = ToPortablePath(settings.GoogleDriveBackupPath, baseDirectory),
                SafetyBackupPath = ToPortablePath(settings.SafetyBackupPath, baseDirectory)
            };

            await using var stream = File.Create(filePath);
            await JsonSerializer.SerializeAsync(stream, portable, JsonOptions, cancellationToken);
        }

        /// <summary>
        /// settings.json lives in the application folder, so its folder is the base for relative paths.
        /// </summary>
        private static string GetBaseDirectory(string settingsFilePath)
        {
            return Path.GetDirectoryName(Path.GetFullPath(settingsFilePath)) ?? AppContext.BaseDirectory;
        }

        public static string ResolvePortablePath(string path, string baseDirectory)
        {
            if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
                return path;

            return Path.GetFullPath(Path.Combine(baseDirectory, path));
        }

        public static string ToPortablePath(string path, string baseDirectory)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
                return path;

            var fullPath = Path.GetFullPath(path);
            var basePrefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory)) + Path.DirectorySeparatorChar;

            return fullPath.StartsWith(basePrefix, StringComparison.OrdinalIgnoreCase)
                ? Path.GetRelativePath(baseDirectory, fullPath)
                : path;
        }
    }
}