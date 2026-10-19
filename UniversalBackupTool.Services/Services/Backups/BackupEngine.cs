using System.IO.Compression;

using UniversalBackupTool.Interface.Interfaces.Backups;
using UniversalBackupTool.Interface.Interfaces.FileSystem;
using UniversalBackupTool.Interfaces.Storage;
using UniversalBackupTool.Model.Models.Settings;

namespace UniversalBackupTool.Service.Services.Backups
{
    public sealed class BackupEngine : IBackupEngine
    {
        private readonly IAppStoragePathsProvider _appStoragePathsProvider;
        private readonly IFileSystemProvider _fileSystemProvider;

        public BackupEngine(IAppStoragePathsProvider appStoragePathsProvider, IFileSystemProvider fileSystemProvider)
        {
            _appStoragePathsProvider = appStoragePathsProvider ?? throw new ArgumentNullException(nameof(appStoragePathsProvider));
            _fileSystemProvider = fileSystemProvider ?? throw new ArgumentNullException(nameof(fileSystemProvider));
        }

        public async Task<string> CreateBackupArchiveAsync(string sourceRootPath, string archivePath, AppSettings settings, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(settings);

            if (string.IsNullOrWhiteSpace(sourceRootPath))
                throw new ArgumentException("Source root path cannot be empty.", nameof(sourceRootPath));

            if (string.IsNullOrWhiteSpace(archivePath))
                throw new ArgumentException("Archive path cannot be empty.", nameof(archivePath));

            Directory.CreateDirectory(Path.GetDirectoryName(archivePath) ?? _appStoragePathsProvider.GetPaths().BaseDirectory);

            var files = await _fileSystemProvider.EnumerateFilesAsync(sourceRootPath, cancellationToken);

            await using var archiveStream = File.Create(archivePath);
            using var zipArchive = new ZipArchive(archiveStream, ZipArchiveMode.Create, leaveOpen: false);

            foreach (var filePath in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var relativePath = _fileSystemProvider.GetRelativePath(sourceRootPath, filePath);
                var entry = zipArchive.CreateEntry(relativePath, GetCompressionLevel(filePath, settings));

                await using var inputStream = File.OpenRead(filePath);
                await using var outputStream = entry.Open();

                await inputStream.CopyToAsync(outputStream, cancellationToken);
            }

            return archivePath;
        }

        public Task<string> ResumeBackupArchiveAsync(string archivePath, string sourceRootPath, AppSettings settings, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(archivePath))
                throw new ArgumentException("Archive path cannot be empty.", nameof(archivePath));

            if (!File.Exists(archivePath))
                return CreateBackupArchiveAsync(sourceRootPath, archivePath, settings, cancellationToken);

            var files = Directory.Exists(sourceRootPath) ? Directory.EnumerateFiles(sourceRootPath, "*", SearchOption.AllDirectories).ToList() : [];

            using var zipArchive = ZipFile.Open(archivePath, ZipArchiveMode.Update);

            foreach (var filePath in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var relativePath = _fileSystemProvider.GetRelativePath(sourceRootPath, filePath);

                if (zipArchive.Entries.Any(entry => string.Equals(entry.FullName.Replace('\\', '/'), relativePath, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var entry = zipArchive.CreateEntry(relativePath, GetCompressionLevel(filePath, settings));

                using var inputStream = File.OpenRead(filePath);
                using var outputStream = entry.Open();

                inputStream.CopyTo(outputStream);
            }

            return Task.FromResult(archivePath);
        }

        private static CompressionLevel GetCompressionLevel(string filePath, AppSettings settings)
        {
            var fileLength = new FileInfo(filePath).Length;
            var matchedRule = settings.CompressionRules
                .OrderBy(rule => rule.MaxSizeBytes)
                .FirstOrDefault(rule => fileLength <= rule.MaxSizeBytes);

            return matchedRule?.CompressionLevel ?? CompressionLevel.NoCompression;
        }
    }
}