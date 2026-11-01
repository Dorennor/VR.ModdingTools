using System.Security.Cryptography;

using UniversalBackupTool.Interface.Interfaces.Backups;
using UniversalBackupTool.Interface.Interfaces.FileSystem;
using UniversalBackupTool.Interfaces.Snapshots;
using UniversalBackupTool.Model.Models.Backups;
using UniversalBackupTool.Model.Models.FileSystem;
using UniversalBackupTool.Model.Models.Snapshots;

namespace UniversalBackupTool.Service.Services.Snapshots
{
    public sealed class SnapshotService : ISnapshotService
    {
        private readonly IFileSystemProvider _fileSystemProvider;
        private readonly ISafetyBackupService _safetyBackupService;

        public SnapshotService(IFileSystemProvider fileSystemProvider, ISafetyBackupService safetyBackupService)
        {
            _fileSystemProvider = fileSystemProvider ?? throw new ArgumentNullException(nameof(fileSystemProvider));
            _safetyBackupService = safetyBackupService ?? throw new ArgumentNullException(nameof(safetyBackupService));
        }

        public async Task<SnapshotData> CreateSnapshotAsync(
            Guid profileId,
            string rootPath,
            CancellationToken cancellationToken = default,
            IProgress<BackupProgress>? progress = null)
        {
            var scanned = await ScanAsync(profileId, rootPath, cancellationToken);
            var files = scanned.Files.Values.Where(file => file.Kind == FileEntryKind.File).ToList();
            var totalBytes = files.Sum(file => file.SizeBytes);
            var bytesDone = 0L;
            var filesDone = 0;

            var throttle = new ProgressThrottle(progress);
            throttle.Report(new BackupProgress("Hashing game files", null, 0, totalBytes, 0, files.Count), force: true);

            // The hashes let later comparisons tell a rewritten but identical file (reinstall, file verification) from a
            // really modified one. Links are not hashed: their data belongs to the Vortex staging folder.
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var hash = TryComputeHash(Path.Combine(rootPath, file.RelativePath), cancellationToken);

                if (hash is not null)
                    scanned.Files[file.RelativePath] = file with { Sha256 = hash };

                bytesDone += file.SizeBytes;
                filesDone++;
                throttle.Report(new BackupProgress("Hashing game files", file.RelativePath, bytesDone, totalBytes, filesDone, files.Count), force: filesDone == files.Count);
            }

            // Folders are recorded too, so Purge keeps vanilla folders (also empty ones) and removes the ones mods added.
            return scanned with { Directories = ListRealDirectories(rootPath, cancellationToken) };
        }

        public Task<IReadOnlyList<string>> GetUnregisteredDirectoriesAsync(string rootPath, SnapshotData baselineSnapshot, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(baselineSnapshot);

            var vanilla = GetVanillaDirectories(baselineSnapshot);
            IReadOnlyList<string> unregistered = ListRealDirectories(rootPath, cancellationToken)
                .Where(directory => !vanilla.Contains(directory))
                .OrderByDescending(directory => directory.Count(character => character == '/'))
                .ThenBy(directory => directory, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return Task.FromResult(unregistered);
        }

        /// <summary>
        /// Folders recorded in the snapshot plus every folder holding a snapshot entry. Older snapshots record no folders,
        /// so for them only folders holding vanilla files count as vanilla.
        /// </summary>
        private static HashSet<string> GetVanillaDirectories(SnapshotData snapshot)
        {
            var directories = new HashSet<string>(snapshot.Directories ?? [], StringComparer.OrdinalIgnoreCase);

            foreach (var path in snapshot.Files.Keys)
            {
                var normalized = path.Replace('\\', '/');

                for (var separator = normalized.LastIndexOf('/'); separator > 0; separator = normalized.LastIndexOf('/', separator - 1))
                {
                    if (!directories.Add(normalized[..separator]))
                        break;
                }
            }

            return directories;
        }

        /// <summary>
        /// Relative paths of the real folders below the root. Folder links (junctions, symlinks) are entries of their own
        /// and are never entered.
        /// </summary>
        private static List<string> ListRealDirectories(string rootPath, CancellationToken cancellationToken)
        {
            var result = new List<string>();
            var root = new DirectoryInfo(Path.GetFullPath(rootPath));

            if (!root.Exists)
                return result;

            var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = 0 };
            var pending = new Stack<DirectoryInfo>();
            pending.Push(root);

            while (pending.TryPop(out var directory))
            {
                foreach (var subdirectory in directory.EnumerateDirectories("*", options))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (IsDirectoryLink(subdirectory))
                        continue;

                    result.Add(Path.GetRelativePath(root.FullName, subdirectory.FullName).Replace('\\', '/'));
                    pending.Push(subdirectory);
                }
            }

            return result;
        }

        /// <summary>
        /// Same rule as the file system scan: a reparse point with a link target (junction, symlink). Other reparse
        /// points, such as cloud placeholders, are real folders.
        /// </summary>
        private static bool IsDirectoryLink(DirectoryInfo directory)
        {
            try
            {
                return (directory.Attributes & FileAttributes.ReparsePoint) != 0 && directory.LinkTarget is not null;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return true;
            }
        }

        public async Task<Dictionary<string, FileMetadata>> GetChangesAsync(string rootPath, SnapshotData baselineSnapshot, CancellationToken cancellationToken = default)
        {
            var comparison = await CompareAsync(rootPath, baselineSnapshot, verifyContent: false, cancellationToken);
            return comparison.Changes;
        }

        public async Task<SnapshotComparison> CompareAsync(
            string rootPath,
            SnapshotData baselineSnapshot,
            bool verifyContent,
            CancellationToken cancellationToken = default,
            IProgress<BackupProgress>? progress = null)
        {
            ArgumentNullException.ThrowIfNull(baselineSnapshot);

            var currentSnapshot = await ScanAsync(baselineSnapshot.ProfileId, rootPath, cancellationToken);
            var changes = new Dictionary<string, FileMetadata>(StringComparer.OrdinalIgnoreCase);
            var toVerify = new List<(FileMetadata Baseline, FileMetadata Current)>();
            var reanchored = new List<FileMetadata>();
            var unverified = 0;

            foreach (var current in currentSnapshot.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!baselineSnapshot.Files.TryGetValue(current.Key, out var baselineFile))
                {
                    changes[current.Key] = current.Value;
                    continue;
                }

                if (baselineFile.SizeBytes == current.Value.SizeBytes
                    && baselineFile.LastWriteTimeUtc == current.Value.LastWriteTimeUtc
                    && baselineFile.Kind == current.Value.Kind)
                    continue;

                // Only a regular file whose size is unchanged can still have the same content; it is hashed below.
                var contentMayMatch = verifyContent
                    && baselineFile.Kind == FileEntryKind.File
                    && current.Value.Kind == FileEntryKind.File
                    && baselineFile.SizeBytes == current.Value.SizeBytes;

                if (contentMayMatch && baselineFile.Sha256 is not null)
                {
                    toVerify.Add((baselineFile, current.Value));
                    continue;
                }

                if (contentMayMatch)
                    unverified++;

                changes[current.Key] = current.Value;
            }

            foreach (var baseline in baselineSnapshot.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!currentSnapshot.Files.ContainsKey(baseline.Key))
                    changes[baseline.Key] = baseline.Value;
            }

            var totalBytes = toVerify.Sum(pair => pair.Current.SizeBytes);
            var throttle = new ProgressThrottle(progress);
            var bytesDone = 0L;
            var filesDone = 0;

            foreach (var (baselineFile, currentFile) in toVerify)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var hash = TryComputeHash(Path.Combine(rootPath, currentFile.RelativePath), cancellationToken);

                if (hash is not null && string.Equals(hash, baselineFile.Sha256, StringComparison.OrdinalIgnoreCase))
                    reanchored.Add(baselineFile with { LastWriteTimeUtc = currentFile.LastWriteTimeUtc });
                else
                    changes[currentFile.RelativePath] = currentFile with { Sha256 = hash };

                bytesDone += currentFile.SizeBytes;
                filesDone++;
                throttle.Report(new BackupProgress("Comparing file contents with the snapshot", currentFile.RelativePath, bytesDone, totalBytes, filesDone, toVerify.Count), force: filesDone == toVerify.Count);
            }

            return new SnapshotComparison(changes, reanchored, unverified);
        }

        /// <summary>
        /// Metadata of every entry, without hashes. Game folders are never scanned through links: links (Vortex
        /// deployments) are recorded as entries.
        /// </summary>
        private async Task<SnapshotData> ScanAsync(Guid profileId, string rootPath, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(rootPath))
                throw new ArgumentException("Root path cannot be empty.", nameof(rootPath));

            var dictionary = new Dictionary<string, FileMetadata>(StringComparer.OrdinalIgnoreCase);

            if (!Directory.Exists(rootPath))
                return new SnapshotData(profileId, dictionary);

            var entries = await Task.Run(() => _fileSystemProvider.ScanWithoutFollowingLinks(rootPath, cancellationToken), cancellationToken);

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                dictionary[entry.RelativePath] = new FileMetadata(
                    RelativePath: entry.RelativePath,
                    SizeBytes: entry.Length,
                    LastWriteTimeUtc: entry.LastWriteTimeUtc,
                    IsSymlink: entry.Kind is FileEntryKind.FileSymlink or FileEntryKind.DirectoryLink)
                {
                    Kind = entry.Kind
                };
            }

            return new SnapshotData(profileId, dictionary);
        }

        /// <summary>
        /// SHA-256 of a file as uppercase hex, or null when the file cannot be read (in use, access denied, removed).
        /// </summary>
        private static string? TryComputeHash(string path, CancellationToken cancellationToken)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.SequentialScan);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[1024 * 1024];
                int read;

                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    hash.AppendData(buffer, 0, read);
                }

                return Convert.ToHexString(hash.GetHashAndReset());
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        public async Task<PurgeResult> PurgeUnregisteredFilesAsync(string rootPath, SnapshotData baselineSnapshot, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(rootPath))
                throw new ArgumentException("Root path cannot be empty.", nameof(rootPath));

            if (!Directory.Exists(rootPath))
                return new PurgeResult(0, null);

            ArgumentNullException.ThrowIfNull(baselineSnapshot);

            // Not following links: entries below directory links are never visited, so link targets are never touched.
            var entries = await Task.Run(() => _fileSystemProvider.ScanWithoutFollowingLinks(rootPath, cancellationToken), cancellationToken);
            var toDelete = entries
                .Where(entry => !baselineSnapshot.Files.ContainsKey(entry.RelativePath))
                .ToList();

            var safetyBackupFiles = SelectEntriesNeedingSafetyCopy(toDelete)
                .Select(entry => new ArchiveFileItem(entry.FullPath, entry.RelativePath))
                .ToList();

            var safetyBackupPath = await _safetyBackupService.CreateSafetyBackupAsync(safetyBackupFiles, "pre-purge", "Purge", cancellationToken);
            var deleted = 0;
            var linksRemoved = 0;

            foreach (var entry in toDelete)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _fileSystemProvider.DeleteEntry(entry);
                deleted++;

                if (entry.Kind != FileEntryKind.File)
                    linksRemoved++;
            }

            // Deepest first, so a folder whose subfolders were just removed is empty when its turn comes. Empty folders
            // hold no data, so they need no safety copy.
            var foldersRemoved = 0;
            var foldersKept = 0;

            foreach (var relative in await GetUnregisteredDirectoriesAsync(rootPath, baselineSnapshot, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fullPath = Path.Combine(rootPath, relative.Replace('/', Path.DirectorySeparatorChar));

                try
                {
                    if (Directory.EnumerateFileSystemEntries(fullPath).Any())
                    {
                        foldersKept++;
                        continue;
                    }

                    Directory.Delete(fullPath, recursive: false);
                    foldersRemoved++;
                }
                catch (DirectoryNotFoundException)
                {
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    foldersKept++;
                }
            }

            return new PurgeResult(deleted, safetyBackupPath)
            {
                LinksRemoved = linksRemoved,
                FoldersRemoved = foldersRemoved,
                FoldersKept = foldersKept
            };
        }

        /// <summary>
        /// Deleting a link loses no data, so links get no safety copy: symlinks and junctions only point elsewhere, and a
        /// hardlink's data stays reachable through its other names (usually the Vortex staging file). The exception is a
        /// file whose every hardlink name is being deleted here; one of its names is copied.
        /// </summary>
        private static IEnumerable<ScannedEntry> SelectEntriesNeedingSafetyCopy(IReadOnlyList<ScannedEntry> toDelete)
        {
            foreach (var entry in toDelete.Where(entry => entry.Kind == FileEntryKind.File))
                yield return entry;

            foreach (var group in toDelete.Where(entry => entry.Kind == FileEntryKind.HardLink).GroupBy(entry => entry.FileId ?? entry.FullPath, StringComparer.OrdinalIgnoreCase))
            {
                var first = group.First();

                if (first.FileId is null || group.Count() >= first.LinkCount)
                    yield return first;
            }
        }

        /// <summary>
        /// Hashing goes file by file and a game folder can hold a huge number of small files, so reports are limited to
        /// about ten per second.
        /// </summary>
        private sealed class ProgressThrottle(IProgress<BackupProgress>? target)
        {
            private readonly System.Diagnostics.Stopwatch _stopwatch = System.Diagnostics.Stopwatch.StartNew();
            private long _lastReportMilliseconds = -1000;

            public void Report(BackupProgress value, bool force = false)
            {
                if (target is null || (!force && _stopwatch.ElapsedMilliseconds - _lastReportMilliseconds < 100))
                    return;

                _lastReportMilliseconds = _stopwatch.ElapsedMilliseconds;
                target.Report(value);
            }
        }
    }
}