using System.Diagnostics;
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
        /// <summary>
        /// Shown while vanilla files with new timestamps are read: nothing is archived yet, and the result is remembered,
        /// so the same files are not read again.
        /// </summary>
        internal const string VerifyStepName = "Checking vanilla files with new timestamps (nothing is archived in this step; done once)";

        private const string UpgradeStepName = "Adding missing details to the snapshot";

        private const string RelocationStepName = "Looking for moved or copied vanilla files";

        /// <summary>
        /// The file types that carry a version resource. Other files are never opened for it.
        /// </summary>
        private static readonly HashSet<string> VersionedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".exe", ".dll", ".asi", ".sys", ".ocx"
        };

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

                var fullPath = Path.Combine(rootPath, file.RelativePath);

                scanned.Files[file.RelativePath] = file with
                {
                    Sha256 = TryComputeHash(fullPath, cancellationToken),
                    FileVersion = TryReadFileVersion(fullPath)
                };

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
        internal static HashSet<string> GetVanillaDirectories(SnapshotData snapshot)
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
        internal static List<string> ListRealDirectories(string rootPath, CancellationToken cancellationToken)
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
        internal static bool IsDirectoryLink(DirectoryInfo directory)
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
            IProgress<BackupProgress>? progress = null,
            Action<FileMetadata>? onReanchored = null)
        {
            ArgumentNullException.ThrowIfNull(baselineSnapshot);

            var currentSnapshot = await ScanAsync(baselineSnapshot.ProfileId, rootPath, cancellationToken);
            var changes = new Dictionary<string, FileMetadata>(StringComparer.OrdinalIgnoreCase);
            var toVerify = new List<(FileMetadata Baseline, FileMetadata Current)>();
            var reanchored = new List<FileMetadata>();
            var unverified = 0;
            var versionChanged = 0;
            var unreadable = 0;

            foreach (var current in currentSnapshot.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!baselineSnapshot.Files.TryGetValue(current.Key, out var baselineFile))
                {
                    changes[current.Key] = current.Value;
                    continue;
                }

                // First pass: identical metadata counts as the same file, and nothing is read.
                if (MetadataMatches(baselineFile, current.Value))
                    continue;

                if (verifyContent && HasOwnData(baselineFile) && HasOwnData(current.Value))
                {
                    var currentVersion = baselineFile.FileVersion is null ? null : TryReadFileVersion(Path.Combine(rootPath, current.Value.RelativePath));

                    if (currentVersion is not null && !string.Equals(currentVersion, baselineFile.FileVersion, StringComparison.Ordinal))
                    {
                        versionChanged++;
                        changes[current.Key] = current.Value with { FileVersion = currentVersion };
                        continue;
                    }

                    // Only a file whose size is unchanged can still have the same content; it is hashed below. Timestamps and
                    // the regular/hardlink difference say nothing about the content, so they do not decide anything here.
                    if (baselineFile.SizeBytes == current.Value.SizeBytes)
                    {
                        if (baselineFile.Sha256 is not null)
                        {
                            toVerify.Add((baselineFile, current.Value));
                            continue;
                        }

                        if (baselineFile.Kind == FileEntryKind.File)
                            unverified++;
                    }
                }

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

                var fullPath = Path.Combine(rootPath, currentFile.RelativePath);
                var hash = TryComputeHash(fullPath, cancellationToken);

                if (hash is not null && string.Equals(hash, baselineFile.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    var anchored = Anchor(baselineFile, currentFile, fullPath);
                    reanchored.Add(anchored);
                    onReanchored?.Invoke(anchored);
                }
                else
                {
                    if (hash is null)
                        unreadable++;

                    changes[currentFile.RelativePath] = currentFile with { Sha256 = hash };
                }

                bytesDone += currentFile.SizeBytes;
                filesDone++;
                throttle.Report(new BackupProgress(VerifyStepName, currentFile.RelativePath, bytesDone, totalBytes, filesDone, toVerify.Count), force: filesDone == toVerify.Count);
            }

            var relocated = verifyContent
                ? FindRelocated(rootPath, baselineSnapshot, currentSnapshot.Files, cancellationToken, progress)
                : [];

            return new SnapshotComparison(changes, reanchored, unverified)
            {
                Relocated = relocated,
                VersionChanged = versionChanged,
                UnreadableFiles = unreadable
            };
        }

        public async Task<SnapshotUpgradeResult> UpgradeSnapshotAsync(
            string rootPath,
            SnapshotData baselineSnapshot,
            CancellationToken cancellationToken = default,
            IProgress<BackupProgress>? progress = null,
            Action<FileMetadata>? onUpgraded = null)
        {
            ArgumentNullException.ThrowIfNull(baselineSnapshot);

            var current = (await ScanAsync(baselineSnapshot.ProfileId, rootPath, cancellationToken)).Files;
            var work = new List<(FileMetadata Baseline, FileMetadata Current, bool Hash)>();
            var different = new List<string>();
            int complete = 0, missing = 0, links = 0, cannotProve = 0;

            foreach (var baseline in baselineSnapshot.Files.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // A link recorded without a hash was a deployment when the snapshot was taken; its data is not vanilla data.
                if (!HasOwnData(baseline) || (baseline.Kind != FileEntryKind.File && baseline.Sha256 is null))
                {
                    links++;
                    continue;
                }

                if (!current.TryGetValue(baseline.RelativePath, out var file))
                {
                    missing++;
                    continue;
                }

                // Without a stored hash, nothing could tell a deployed mod file from the vanilla file.
                if (!HasOwnData(file) || (file.Kind != FileEntryKind.File && baseline.Sha256 is null))
                {
                    links++;
                    continue;
                }

                if (baseline.SizeBytes != file.SizeBytes)
                {
                    different.Add(baseline.RelativePath);
                    continue;
                }

                var metadataMatches = MetadataMatches(baseline, file);

                if (metadataMatches && baseline.Sha256 is not null && baseline.CreationTimeUtc is not null)
                    complete++;
                else if (metadataMatches)
                    work.Add((baseline, file, baseline.Sha256 is null));
                else if (baseline.Sha256 is not null)
                    work.Add((baseline, file, true));
                else
                    cannotProve++;
            }

            var totalBytes = work.Where(item => item.Hash).Sum(item => item.Current.SizeBytes);
            var throttle = new ProgressThrottle(progress);
            var bytesDone = 0L;
            var filesDone = 0;
            var upgraded = 0;
            var unreadable = 0;

            throttle.Report(new BackupProgress(UpgradeStepName, null, 0, totalBytes, 0, work.Count), force: true);

            foreach (var (baseline, file, needsHash) in work)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var fullPath = Path.Combine(rootPath, file.RelativePath);
                var hash = needsHash ? TryComputeHash(fullPath, cancellationToken) : baseline.Sha256;

                if (hash is null)
                    unreadable++;

                // A stored hash is the proof. Without one the metadata matched, which is what the first pass trusts too.
                if (baseline.Sha256 is not null && hash is not null && !string.Equals(hash, baseline.Sha256, StringComparison.OrdinalIgnoreCase))
                    different.Add(baseline.RelativePath);
                else if (hash is not null || baseline.Sha256 is null)
                {
                    var entry = Anchor(baseline, file, fullPath) with { Sha256 = baseline.Sha256 ?? hash };
                    upgraded++;
                    onUpgraded?.Invoke(entry);
                }

                if (needsHash)
                    bytesDone += file.SizeBytes;

                filesDone++;
                throttle.Report(new BackupProgress(UpgradeStepName, file.RelativePath, bytesDone, totalBytes, filesDone, work.Count), force: filesDone == work.Count);
            }

            return new SnapshotUpgradeResult(upgraded, complete, missing, links, cannotProve, unreadable, different);
        }

        public async Task<IReadOnlyList<RelocatedFile>> FindRelocatedFilesAsync(
            string rootPath,
            SnapshotData baselineSnapshot,
            CancellationToken cancellationToken = default,
            IProgress<BackupProgress>? progress = null)
        {
            ArgumentNullException.ThrowIfNull(baselineSnapshot);

            var current = await ScanAsync(baselineSnapshot.ProfileId, rootPath, cancellationToken);

            return FindRelocated(rootPath, baselineSnapshot, current.Files, cancellationToken, progress);
        }

        /// <summary>
        /// Regular files at new paths with the content of a vanilla file. Only files with the size of a hashed vanilla file
        /// are read, so this costs next to nothing normally. Empty files all look alike and are skipped. Each vanilla file
        /// counts as moved once; other files with its content are copies.
        /// </summary>
        private static List<RelocatedFile> FindRelocated(
            string rootPath,
            SnapshotData baseline,
            IReadOnlyDictionary<string, FileMetadata> current,
            CancellationToken cancellationToken,
            IProgress<BackupProgress>? progress)
        {
            var bySize = baseline.Files.Values
                .Where(file => file.Sha256 is not null && file.SizeBytes > 0)
                .ToLookup(file => file.SizeBytes);
            var candidates = current.Values
                .Where(file => file.Kind == FileEntryKind.File && !baseline.Files.ContainsKey(file.RelativePath) && bySize.Contains(file.SizeBytes))
                .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var result = new List<RelocatedFile>();
            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var throttle = new ProgressThrottle(progress);
            var totalBytes = candidates.Sum(file => file.SizeBytes);
            var bytesDone = 0L;
            var filesDone = 0;

            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var hash = TryComputeHash(Path.Combine(rootPath, candidate.RelativePath), cancellationToken);
                var matches = hash is null
                    ? []
                    : bySize[candidate.SizeBytes]
                        .Where(file => string.Equals(file.Sha256, hash, StringComparison.OrdinalIgnoreCase))
                        .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                if (matches.Count > 0)
                {
                    var free = matches.FirstOrDefault(file => !current.ContainsKey(file.RelativePath) && !claimed.Contains(file.RelativePath));

                    if (free is not null)
                        claimed.Add(free.RelativePath);

                    result.Add(new RelocatedFile(candidate.RelativePath, (free ?? matches[0]).RelativePath, free is not null));
                }

                bytesDone += candidate.SizeBytes;
                filesDone++;
                throttle.Report(new BackupProgress(RelocationStepName, candidate.RelativePath, bytesDone, totalBytes, filesDone, candidates.Count), force: filesDone == candidates.Count);
            }

            return result;
        }

        /// <summary>
        /// The first pass, which reads no file. Creation time counts only when the snapshot has it.
        /// </summary>
        private static bool MetadataMatches(FileMetadata baseline, FileMetadata current)
        {
            return baseline.SizeBytes == current.SizeBytes
                && baseline.LastWriteTimeUtc == current.LastWriteTimeUtc
                && baseline.Kind == current.Kind
                && (baseline.CreationTimeUtc is null || current.CreationTimeUtc is null || baseline.CreationTimeUtc == current.CreationTimeUtc);
        }

        /// <summary>
        /// Regular files and hardlinks: a name that holds file data, which can be hashed and compared. A hardlink with
        /// vanilla content is simply the vanilla file with one more name.
        /// </summary>
        private static bool HasOwnData(FileMetadata file) => file.Kind is FileEntryKind.File or FileEntryKind.HardLink;

        /// <summary>
        /// The baseline entry of a file proven identical, with what the disk says now. The hash is kept.
        /// </summary>
        private static FileMetadata Anchor(FileMetadata baseline, FileMetadata current, string fullPath)
        {
            return baseline with
            {
                LastWriteTimeUtc = current.LastWriteTimeUtc,
                CreationTimeUtc = current.CreationTimeUtc,
                Kind = current.Kind,
                IsSymlink = false,
                FileVersion = baseline.FileVersion ?? TryReadFileVersion(fullPath)
            };
        }

        /// <summary>
        /// File version from the version resource, or null for other file types, files without one and unreadable files.
        /// Uses the numeric version: the text version is free-form and can differ by language.
        /// </summary>
        internal static string? TryReadFileVersion(string path)
        {
            if (!VersionedExtensions.Contains(Path.GetExtension(path)))
                return null;

            try
            {
                var info = FileVersionInfo.GetVersionInfo(path);

                if (info.FileMajorPart != 0 || info.FileMinorPart != 0 || info.FileBuildPart != 0 || info.FilePrivatePart != 0)
                    return $"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}.{info.FilePrivatePart}";

                return string.IsNullOrWhiteSpace(info.FileVersion) ? null : info.FileVersion.Trim();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return null;
            }
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
                    Kind = entry.Kind,
                    CreationTimeUtc = entry.CreationTimeUtc
                };
            }

            return new SnapshotData(profileId, dictionary);
        }

        /// <summary>
        /// SHA-256 of a file as uppercase hex, or null when the file cannot be read (in use, access denied, removed).
        /// </summary>
        internal static string? TryComputeHash(string path, CancellationToken cancellationToken)
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

            // Deleting a moved vanilla file would lose it from the game; it goes back to its snapshot path instead.
            var current = (await ScanAsync(baselineSnapshot.ProfileId, rootPath, cancellationToken)).Files;
            var movedBack = FindRelocated(rootPath, baselineSnapshot, current, cancellationToken, null)
                .Where(file => file.OriginalMissing)
                .ToDictionary(file => file.RelativePath, file => file.OriginalPath, StringComparer.OrdinalIgnoreCase);

            var toDelete = entries
                .Where(entry => !baselineSnapshot.Files.ContainsKey(entry.RelativePath) && !movedBack.ContainsKey(entry.RelativePath))
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

            var notMovedBack = new List<string>();

            foreach (var (relative, original) in movedBack)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = Path.Combine(rootPath, original.Replace('/', Path.DirectorySeparatorChar));

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Move(Path.Combine(rootPath, relative.Replace('/', Path.DirectorySeparatorChar)), destination, overwrite: false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    notMovedBack.Add(relative);
                }
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
                FoldersKept = foldersKept,
                FilesMovedBack = movedBack.Count - notMovedBack.Count,
                FilesNotMovedBack = notMovedBack
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