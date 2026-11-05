using UniversalBackupTool.Interface.Interfaces.FileSystem;
using UniversalBackupTool.Model.Models.FileSystem;

namespace UniversalBackupTool.Services
{
    public sealed class FileSystemProvider : IFileSystemProvider
    {
        private static string GetRelativePath(string rootPath, string filePath)
        {
            var fullRoot = Path.GetFullPath(rootPath);
            var fullFile = Path.GetFullPath(filePath);

            if (string.Equals(fullRoot, fullFile, StringComparison.OrdinalIgnoreCase))
                return string.Empty;

            var relative = Path.GetRelativePath(fullRoot, fullFile);

            return relative.Replace('\\', '/');
        }

        private static readonly EnumerationOptions SingleLevel = new()
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false
        };

        public IReadOnlyList<ScannedEntry> ScanWithoutFollowingLinks(string rootPath, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(rootPath))
                throw new ArgumentException("Root path cannot be empty.", nameof(rootPath));

            var root = new DirectoryInfo(Path.GetFullPath(rootPath));
            var entries = new List<ScannedEntry>();

            if (!root.Exists)
                return entries;

            var pending = new Stack<DirectoryInfo>();
            pending.Push(root);

            while (pending.TryPop(out var directory))
            {
                foreach (var info in directory.EnumerateFileSystemInfos("*", SingleLevel))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var relative = GetRelativePath(root.FullName, info.FullName);

                    if (info is DirectoryInfo subdirectory)
                    {
                        if (IsLink(subdirectory))
                            entries.Add(new ScannedEntry(subdirectory.FullName, relative, FileEntryKind.DirectoryLink, 0, SafeLastWriteUtc(subdirectory), 0, null));
                        else
                            pending.Push(subdirectory);

                        continue;
                    }

                    var file = (FileInfo)info;

                    if (IsLink(file))
                    {
                        entries.Add(new ScannedEntry(file.FullName, relative, FileEntryKind.FileSymlink, 0, SafeLastWriteUtc(file), 0, null));
                        continue;
                    }

                    var (linkCount, fileId) = NativeFileInfo.GetLinkInfo(file.FullName);
                    var kind = linkCount > 1 ? FileEntryKind.HardLink : FileEntryKind.File;
                    entries.Add(new ScannedEntry(file.FullName, relative, kind, SafeLength(file), SafeLastWriteUtc(file), linkCount, fileId) { CreationTimeUtc = SafeCreationUtc(file) });
                }
            }

            return entries;
        }

        public FollowedScan ScanFollowingLinks(string rootPath, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(rootPath))
                throw new ArgumentException("Root path cannot be empty.", nameof(rootPath));

            var files = new List<FollowedFile>();
            var warnings = new List<string>();
            var linksFollowed = 0;
            var emptyDirectories = new List<string>();
            var root = new DirectoryInfo(Path.GetFullPath(rootPath));

            if (!root.Exists)
                return new FollowedScan(files, warnings, 0);

            // Real (link-resolved) directories already included, to stop loops and avoid archiving a directory twice.
            var included = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var rootReal = ResolveDirectory(root) ?? root.FullName;
            included[rootReal] = root.FullName;

            var pending = new Stack<(DirectoryInfo Directory, string RealPath)>();
            pending.Push((root, rootReal));

            while (pending.TryPop(out var current))
            {
                var isEmpty = true;

                foreach (var info in current.Directory.EnumerateFileSystemInfos("*", SingleLevel))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    isEmpty = false;
                    var relative = GetRelativePath(root.FullName, info.FullName);

                    if (info is DirectoryInfo subdirectory)
                    {
                        var isLink = IsLink(subdirectory);
                        string realPath;

                        if (isLink)
                        {
                            var target = ResolveDirectory(subdirectory);

                            if (target is null)
                            {
                                warnings.Add($"Skipped broken directory link: {subdirectory.FullName}");
                                continue;
                            }

                            realPath = target;
                        }
                        else
                        {
                            realPath = Path.Combine(current.RealPath, subdirectory.Name);
                        }

                        // A link to a folder that is (or lies inside) an included folder would loop or duplicate files.
                        // Ordinary sub-folders only need the exact check, which stops loops entered through a link.
                        if (included.TryGetValue(realPath, out var includedAs)
                            || (isLink && IsInsideAny(realPath, included.Keys, out includedAs)))
                        {
                            warnings.Add($"Skipped a folder that is already included: {subdirectory.FullName} (same as {includedAs})");
                            continue;
                        }

                        if (isLink)
                            linksFollowed++;

                        included[realPath] = subdirectory.FullName;
                        pending.Push((subdirectory, realPath));
                        continue;
                    }

                    var file = (FileInfo)info;

                    if (IsLink(file))
                    {
                        FileSystemInfo? target;

                        try
                        {
                            target = file.ResolveLinkTarget(returnFinalTarget: true);
                        }
                        catch (IOException)
                        {
                            target = null;
                        }

                        if (target is null || !target.Exists)
                        {
                            warnings.Add($"Skipped broken file link: {file.FullName}");
                            continue;
                        }

                        linksFollowed++;
                        files.Add(new FollowedFile(file.FullName, relative, true));
                        continue;
                    }

                    files.Add(new FollowedFile(file.FullName, relative, false));
                }

                if (isEmpty && !string.Equals(current.Directory.FullName, root.FullName, StringComparison.OrdinalIgnoreCase))
                    emptyDirectories.Add(GetRelativePath(root.FullName, current.Directory.FullName));
            }

            return new FollowedScan(files, warnings, linksFollowed) { EmptyDirectories = emptyDirectories };
        }

        public void DeleteEntry(ScannedEntry entry)
        {
            ArgumentNullException.ThrowIfNull(entry);

            switch (entry.Kind)
            {
                case FileEntryKind.DirectoryLink:
                    // Non-recursive delete of a directory link or junction removes only the link (RemoveDirectory);
                    // the target directory and its files are left untouched.
                    if (Directory.Exists(entry.FullPath) || IsLink(new DirectoryInfo(entry.FullPath)))
                        Directory.Delete(entry.FullPath, recursive: false);
                    break;

                default:
                    // For a file symlink this removes the link; for a hardlink only this name, the data stays
                    // reachable through the other names.
                    var file = new FileInfo(entry.FullPath);

                    if (file.Exists || file.LinkTarget is not null)
                    {
                        if (file.Exists && file.IsReadOnly && entry.Kind == FileEntryKind.File)
                            file.IsReadOnly = false;

                        File.Delete(entry.FullPath);
                    }

                    break;
            }
        }

        /// <summary>
        /// Symbolic links and junctions. Cloud placeholders (OneDrive, Google Drive) are reparse points too but have no
        /// link target, so they are treated as ordinary files and directories.
        /// </summary>
        private static bool IsLink(FileSystemInfo info)
        {
            try
            {
                return (info.Attributes & FileAttributes.ReparsePoint) != 0 && info.LinkTarget is not null;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static string? ResolveDirectory(DirectoryInfo directory)
        {
            if (!IsLink(directory))
                return Path.TrimEndingDirectorySeparator(directory.FullName);

            try
            {
                var target = directory.ResolveLinkTarget(returnFinalTarget: true);
                return target is { Exists: true } ? Path.TrimEndingDirectorySeparator(target.FullName) : null;
            }
            catch (IOException)
            {
                return null;
            }
        }

        private static bool IsInsideAny(string path, IEnumerable<string> roots, out string includedAs)
        {
            foreach (var root in roots)
            {
                var prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;

                if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    includedAs = root;
                    return true;
                }
            }

            includedAs = string.Empty;
            return false;
        }

        private static long SafeLength(FileInfo file)
        {
            try
            {
                return file.Length;
            }
            catch (IOException)
            {
                return 0;
            }
        }

        private static DateTime? SafeCreationUtc(FileInfo file)
        {
            try
            {
                return file.CreationTimeUtc;
            }
            catch (IOException)
            {
                return null;
            }
        }

        private static DateTime SafeLastWriteUtc(FileSystemInfo info)
        {
            try
            {
                return info.LastWriteTimeUtc;
            }
            catch (IOException)
            {
                return DateTime.MinValue;
            }
        }
    }
}