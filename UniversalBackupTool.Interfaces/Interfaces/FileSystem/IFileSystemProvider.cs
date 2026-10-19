namespace UniversalBackupTool.Interfaces;

public interface IFileSystemProvider
{
    Task EnsureDirectoryExistsAsync(string path, CancellationToken cancellationToken = default);

    Task<IEnumerable<string>> EnumerateFilesAsync(string rootPath, CancellationToken cancellationToken = default);

    string GetRelativePath(string rootPath, string filePath);

    bool IsSymlink(string path);

    string ResolveActualPath(string path);

    Task DeleteFileAsync(string path, CancellationToken cancellationToken = default);

    Task DeleteDirectoryAsync(string path, bool recursive = true, CancellationToken cancellationToken = default);
}