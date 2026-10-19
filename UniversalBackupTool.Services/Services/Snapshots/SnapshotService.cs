using UniversalBackupTool.Interfaces;
using UniversalBackupTool.Models;

namespace UniversalBackupTool.Services;

public sealed class SnapshotService : ISnapshotService
{
    private readonly IFileSystemProvider _fileSystemProvider;
    private readonly ISafetyBackupService _safetyBackupService;

    public SnapshotService(IFileSystemProvider fileSystemProvider, ISafetyBackupService safetyBackupService)
    {
        _fileSystemProvider = fileSystemProvider ?? throw new ArgumentNullException(nameof(fileSystemProvider));
        _safetyBackupService = safetyBackupService ?? throw new ArgumentNullException(nameof(safetyBackupService));
    }

    public async Task<SnapshotData> CreateSnapshotAsync(Guid profileId, string rootPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            throw new ArgumentException("Root path cannot be empty.", nameof(rootPath));

        if (!Directory.Exists(rootPath))
            return new SnapshotData(profileId, new Dictionary<string, FileMetadata>(StringComparer.OrdinalIgnoreCase));

        var files = await _fileSystemProvider.EnumerateFilesAsync(rootPath, cancellationToken);
        var dictionary = new Dictionary<string, FileMetadata>(StringComparer.OrdinalIgnoreCase);

        foreach (var filePath in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativePath = _fileSystemProvider.GetRelativePath(rootPath, filePath);
            var info = new FileInfo(filePath);
            dictionary[relativePath] = new FileMetadata(
                RelativePath: relativePath,
                SizeBytes: info.Length,
                LastWriteTimeUtc: info.LastWriteTimeUtc,
                IsSymlink: _fileSystemProvider.IsSymlink(filePath));
        }

        return new SnapshotData(profileId, dictionary);
    }

    public Task<Dictionary<string, FileMetadata>> GetChangesAsync(string rootPath, SnapshotData baselineSnapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(baselineSnapshot);

        var currentSnapshot = CreateSnapshotAsync(baselineSnapshot.ProfileId, rootPath, cancellationToken)
            .GetAwaiter()
            .GetResult();

        var changes = new Dictionary<string, FileMetadata>(StringComparer.OrdinalIgnoreCase);

        foreach (var current in currentSnapshot.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!baselineSnapshot.Files.TryGetValue(current.Key, out var baselineFile))
            {
                changes[current.Key] = current.Value;
                continue;
            }

            if (baselineFile.SizeBytes != current.Value.SizeBytes || baselineFile.LastWriteTimeUtc != current.Value.LastWriteTimeUtc)
            {
                changes[current.Key] = current.Value;
            }
        }

        foreach (var baseline in baselineSnapshot.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!currentSnapshot.Files.ContainsKey(baseline.Key))
            {
                changes[baseline.Key] = baseline.Value;
            }
        }

        return Task.FromResult(changes);
    }

    public async Task PurgeUnregisteredFilesAsync(string rootPath, SnapshotData baselineSnapshot, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            throw new ArgumentException("Root path cannot be empty.", nameof(rootPath));

        if (!Directory.Exists(rootPath))
            return;

        await _safetyBackupService.CreateSafetyBackupAsync(rootPath, "pre-purge", cancellationToken);

        var files = await _fileSystemProvider.EnumerateFilesAsync(rootPath, cancellationToken);
        foreach (var filePath in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativePath = _fileSystemProvider.GetRelativePath(rootPath, filePath);
            if (!baselineSnapshot.Files.ContainsKey(relativePath))
            {
                await _fileSystemProvider.DeleteFileAsync(filePath, cancellationToken);
            }
        }
    }
}