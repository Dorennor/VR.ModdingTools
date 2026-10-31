using System.IO.Compression;

using UniversalBackupTool.Interface.Interfaces.Backups;
using UniversalBackupTool.Interface.Interfaces.Settings;
using UniversalBackupTool.Interfaces.Storage;
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

            var paths = _appStoragePathsProvider.GetPaths();
            var settings = await _settingsStore.LoadAsync(paths.SettingsFilePath, cancellationToken);
            var safetyBackupsDirectory = ResolveSafetyBackupsDirectory(settings, paths.SafetyBackupsDirectory);
            Directory.CreateDirectory(safetyBackupsDirectory);

            var safePrefix = string.IsNullOrWhiteSpace(archiveNamePrefix) ? "safety" : archiveNamePrefix.Trim();
            var archiveName = $"{safePrefix}_{DateTime.Now:yyyyMMdd_HHmmss}.zip";
            var archivePath = Path.Combine(safetyBackupsDirectory, archiveName);

            if (!Directory.Exists(sourceRootPath))
                return archivePath;

            await using var archiveStream = File.Create(archivePath);
            using var zipArchive = new ZipArchive(archiveStream, ZipArchiveMode.Create, leaveOpen: false);

            var files = Directory.EnumerateFiles(sourceRootPath, "*", SearchOption.AllDirectories);

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var relativePath = Path.GetRelativePath(sourceRootPath, file).Replace('\\', '/');
                var entry = zipArchive.CreateEntry(relativePath, CompressionLevel.NoCompression);

                await using var inputStream = File.OpenRead(file);
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

            var cutoff = DateTime.Now.AddDays(-settings.SafetyBackupRetentionDays);
            var files = directory.EnumerateFiles("*.zip", SearchOption.TopDirectoryOnly);

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (file.LastWriteTimeUtc < cutoff)
                    file.Delete();
            }

            await Task.CompletedTask;
        }

        private static string ResolveSafetyBackupsDirectory(AppSettings settings, string fallbackDirectory)
        {
            ArgumentNullException.ThrowIfNull(settings);

            return string.IsNullOrWhiteSpace(settings.SafetyBackupPath)
                ? fallbackDirectory
                : settings.SafetyBackupPath.Trim();
        }
    }
}