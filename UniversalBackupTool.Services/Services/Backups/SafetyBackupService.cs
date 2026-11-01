using System.IO.Compression;

using UniversalBackupTool.Interface.Interfaces.Backups;
using UniversalBackupTool.Interface.Interfaces.Settings;
using UniversalBackupTool.Interfaces.Storage;
using UniversalBackupTool.Model.Models.Backups;
using UniversalBackupTool.Model.Models.Settings;

namespace UniversalBackupTool.Services.Backups
{
    public sealed class SafetyBackupService : ISafetyBackupService
    {
        private readonly IAppStoragePathsProvider _appStoragePathsProvider;
        private readonly ISettingsStore _settingsStore;

        public SafetyBackupService(IAppStoragePathsProvider appStoragePathsProvider, ISettingsStore settingsStore)
        {
            _appStoragePathsProvider = appStoragePathsProvider ?? throw new ArgumentNullException(nameof(appStoragePathsProvider));
            _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        }

        public async Task<string> CreateSafetyBackupAsync(string sourceRootPath, string? archiveNamePrefix = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(sourceRootPath))
                throw new ArgumentException("Source root path cannot be empty.", nameof(sourceRootPath));

            var safePrefix = string.IsNullOrWhiteSpace(archiveNamePrefix) ? "safety" : archiveNamePrefix.Trim();

            if (!Directory.Exists(sourceRootPath))
            {
                var directory = await GetCategoryDirectoryAsync("General", cancellationToken);
                return Path.Combine(directory, $"{safePrefix}_{DateTime.Now:yyyyMMdd_HHmmss}.zip");
            }

            var files = Directory.EnumerateFiles(sourceRootPath, "*", SearchOption.AllDirectories)
                .Select(path => new ArchiveFileItem(path, Path.GetRelativePath(sourceRootPath, path).Replace('\\', '/')))
                .ToList();

            if (files.Count == 0)
            {
                var directory = await GetCategoryDirectoryAsync("General", cancellationToken);
                var archivePath = Path.Combine(directory, $"{safePrefix}_{DateTime.Now:yyyyMMdd_HHmmss}.zip");
                await using var stream = File.Create(archivePath);
                using var _ = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false);
                return archivePath;
            }

            return (await CreateSafetyBackupAsync(files, safePrefix, "General", cancellationToken))!;
        }

        public async Task<string?> CreateSafetyBackupAsync(
            IReadOnlyList<ArchiveFileItem> files,
            string archiveNamePrefix,
            string category,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(files);

            if (files.Count == 0)
                return null;

            if (string.IsNullOrWhiteSpace(archiveNamePrefix))
                throw new ArgumentException("Archive name prefix cannot be empty.", nameof(archiveNamePrefix));

            if (string.IsNullOrWhiteSpace(category))
                throw new ArgumentException("Category cannot be empty.", nameof(category));

            var directory = await GetCategoryDirectoryAsync(category, cancellationToken);
            var archivePath = Path.Combine(directory, $"{archiveNamePrefix.Trim()}_{DateTime.Now:yyyyMMdd_HHmmss}.zip");

            await using var archiveStream = File.Create(archivePath);
            using var zipArchive = new ZipArchive(archiveStream, ZipArchiveMode.Create, leaveOpen: false);

            foreach (var item in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!File.Exists(item.SourcePath))
                    continue;

                var entry = zipArchive.CreateEntry(item.EntryName.Replace('\\', '/'), CompressionLevel.NoCompression);

                await using var inputStream = new FileStream(item.SourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                await using var outputStream = entry.Open();

                await inputStream.CopyToAsync(outputStream, cancellationToken);
            }

            return archivePath;
        }

        public async Task CleanupExpiredAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(settings);

            var paths = _appStoragePathsProvider.GetPaths();
            var safetyBackupsDirectory = ResolveSafetyBackupsDirectory(settings, paths.SafetyBackupsDirectory);
            var directory = new DirectoryInfo(safetyBackupsDirectory);

            if (!directory.Exists)
                return;

            var cutoff = DateTime.UtcNow.AddDays(-settings.SafetyBackupRetentionDays);
            var files = directory.EnumerateFiles("*.zip", SearchOption.AllDirectories);

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (file.LastWriteTimeUtc < cutoff)
                    file.Delete();
            }

            await Task.CompletedTask;
        }

        private async Task<string> GetCategoryDirectoryAsync(string category, CancellationToken cancellationToken)
        {
            var paths = _appStoragePathsProvider.GetPaths();
            var settings = await _settingsStore.LoadAsync(paths.SettingsFilePath, cancellationToken);
            var safetyBackupsDirectory = ResolveSafetyBackupsDirectory(settings, paths.SafetyBackupsDirectory);
            var categoryDirectory = Path.Combine(safetyBackupsDirectory, SanitizeFolderName(category));
            Directory.CreateDirectory(categoryDirectory);

            return categoryDirectory;
        }

        private static string SanitizeFolderName(string value)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sanitized = new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();
            return sanitized.Length == 0 ? "General" : sanitized;
        }

        private static string ResolveSafetyBackupsDirectory(AppSettings settings, string fallbackDirectory)
        {
            ArgumentNullException.ThrowIfNull(settings);

            var configuredPath = settings.SafetyBackupPath?.Trim();
            var backupRoot = settings.GoogleDriveBackupPath?.Trim();

            if (string.IsNullOrWhiteSpace(configuredPath))
                return fallbackDirectory;

            if (PathsEqual(configuredPath, backupRoot))
                return Path.Combine(configuredPath, "SafetyBackups");

            return configuredPath;
        }

        private static bool PathsEqual(string? left, string? right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
                return false;

            try
            {
                var leftFull = Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var rightFull = Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return string.Equals(leftFull, rightFull, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return string.Equals(left.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), right.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}