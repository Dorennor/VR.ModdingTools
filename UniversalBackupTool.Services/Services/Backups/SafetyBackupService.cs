using System.IO.Compression;

using UniversalBackupTool.Interfaces;
using UniversalBackupTool.Models;

namespace UniversalBackupTool.Services;

public sealed class SafetyBackupService : ISafetyBackupService
{
    private readonly IAppStoragePathsProvider _appStoragePathsProvider;

    public SafetyBackupService(IAppStoragePathsProvider appStoragePathsProvider)
    {
        _appStoragePathsProvider = appStoragePathsProvider ?? throw new ArgumentNullException(nameof(appStoragePathsProvider));
    }

    public async Task<string> CreateSafetyBackupAsync(
        string sourceRootPath,
        string? archiveNamePrefix = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceRootPath))
            throw new ArgumentException("Source root path cannot be empty.", nameof(sourceRootPath));

        var paths = _appStoragePathsProvider.GetPaths();
        Directory.CreateDirectory(paths.SafetyBackupsDirectory);

        var safePrefix = string.IsNullOrWhiteSpace(archiveNamePrefix) ? "safety" : archiveNamePrefix.Trim();
        var archiveName = $"{safePrefix}_{DateTime.Now:yyyyMMdd_HHmmss}.zip";
        var archivePath = Path.Combine(paths.SafetyBackupsDirectory, archiveName);

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
        var directory = new DirectoryInfo(paths.SafetyBackupsDirectory);
        if (!directory.Exists)
            return;

        var cutoff = DateTime.Now.AddDays(-settings.SafetyBackupRetentionDays);
        var files = directory.EnumerateFiles("*.zip", SearchOption.TopDirectoryOnly);

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (file.LastWriteTimeUtc < cutoff)
            {
                file.Delete();
            }
        }

        await Task.CompletedTask;
    }
}