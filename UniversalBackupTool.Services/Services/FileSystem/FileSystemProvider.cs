using UniversalBackupTool.Interface.Interfaces.FileSystem;

namespace UniversalBackupTool.Services
{
    public sealed class FileSystemProvider : IFileSystemProvider
    {
        public async Task EnsureDirectoryExistsAsync(string path, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Path cannot be empty.", nameof(path));

            await Task.Run(() => Directory.CreateDirectory(path), cancellationToken);
        }

        public Task<IEnumerable<string>> EnumerateFilesAsync(string rootPath, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(rootPath))
                throw new ArgumentException("Root path cannot be empty.", nameof(rootPath));

            if (!Directory.Exists(rootPath))
                return Task.FromResult<IEnumerable<string>>([]);

            var files = Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories).Where(file => !IsSymlink(file)).ToList();

            return Task.FromResult<IEnumerable<string>>(files);
        }

        public string GetRelativePath(string rootPath, string filePath)
        {
            var fullRoot = Path.GetFullPath(rootPath);
            var fullFile = Path.GetFullPath(filePath);

            if (string.Equals(fullRoot, fullFile, StringComparison.OrdinalIgnoreCase))
                return string.Empty;

            var relative = Path.GetRelativePath(fullRoot, fullFile);

            return relative.Replace('\\', '/');
        }

        public bool IsSymlink(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return false;

            try
            {
                var attributes = File.GetAttributes(path);
                return attributes.HasFlag(FileAttributes.ReparsePoint);
            }
            catch
            {
                return false;
            }
        }

        public string ResolveActualPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Path cannot be empty.", nameof(path));

            if (!File.Exists(path))
                return Path.GetFullPath(path);

            var linkTarget = File.ResolveLinkTarget(path, returnFinalTarget: true);

            return linkTarget is not null ? linkTarget.FullName : Path.GetFullPath(path);
        }

        public Task DeleteFileAsync(string path, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Path cannot be empty.", nameof(path));

            if (File.Exists(path))
                File.Delete(path);

            return Task.CompletedTask;
        }

        public Task DeleteDirectoryAsync(string path, bool recursive = true, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Path cannot be empty.", nameof(path));

            if (Directory.Exists(path))
                Directory.Delete(path, recursive);

            return Task.CompletedTask;
        }
    }
}