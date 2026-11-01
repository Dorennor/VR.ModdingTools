using System.Diagnostics;

using UniversalBackupTool.Interface.Interfaces.Backups;
using UniversalBackupTool.Interface.Interfaces.FileSystem;
using UniversalBackupTool.Interfaces.Snapshots;
using UniversalBackupTool.Model.Models.Backups;
using UniversalBackupTool.Model.Models.FileSystem;
using UniversalBackupTool.Model.Models.Profiles;
using UniversalBackupTool.Model.Models.Snapshots;
using UniversalBackupTool.Service.Services.Backups;

namespace UniversalBackupTool.Service.Services.Snapshots
{
    public sealed class TrackedFilesPurgeService : ITrackedFilesPurgeService
    {
        private static readonly BackupComponent[] TrackedComponents = [BackupComponent.GameChanges, BackupComponent.GameConfigs];

        private readonly IFileSystemProvider _fileSystemProvider;
        private readonly ISafetyBackupService _safetyBackupService;

        public TrackedFilesPurgeService(IFileSystemProvider fileSystemProvider, ISafetyBackupService safetyBackupService)
        {
            _fileSystemProvider = fileSystemProvider ?? throw new ArgumentNullException(nameof(fileSystemProvider));
            _safetyBackupService = safetyBackupService ?? throw new ArgumentNullException(nameof(safetyBackupService));
        }

        public async Task<TrackedPurgePlan> BuildPlanAsync(
            BackupProfile profile,
            string rootPath,
            SnapshotData baselineSnapshot,
            string backupRoot,
            IProgress<BackupProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(profile);
            ArgumentNullException.ThrowIfNull(baselineSnapshot);
            ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
            ArgumentException.ThrowIfNullOrWhiteSpace(backupRoot);

            var backups = new List<LocatedBackupManifest>();

            foreach (var component in TrackedComponents)
            {
                var directory = BackupLocations.GetComponentDirectory(backupRoot, profile, component);
                var latest = await BackupLocations.FindLatestCompleteManifestAsync(directory, profile, cancellationToken);

                if (latest is not null)
                    backups.Add(latest);
            }

            var (trackedFiles, trackedFolders) = CollectTracked(backups);

            if (trackedFiles.Count == 0 && trackedFolders.Count == 0)
                return new TrackedPurgePlan(rootPath, backups, [], [], 0);

            // Not following links: what Vortex deployed shows up as link entries and is left to Vortex.
            var entries = (await Task.Run(() => _fileSystemProvider.ScanWithoutFollowingLinks(rootPath, cancellationToken), cancellationToken))
                .ToDictionary(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase);
            var vanillaFiles = new HashSet<string>(baselineSnapshot.Files.Keys.Select(path => path.Replace('\\', '/')), StringComparer.OrdinalIgnoreCase);

            var present = trackedFiles
                .Where(item => entries.ContainsKey(item.Key))
                .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var toHash = present
                .Where(item => entries[item.Key].Kind == FileEntryKind.File && !vanillaFiles.Contains(item.Key))
                .ToList();
            var totalBytes = toHash.Sum(item => entries[item.Key].Length);
            var bytesDone = 0L;
            var filesDone = 0;
            var stopwatch = Stopwatch.StartNew();
            var lastReport = -1000L;

            var files = new List<TrackedFile>();

            foreach (var (relativePath, versions) in present)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var entry = entries[relativePath];
                var status = Classify(rootPath, relativePath, versions, entry, vanillaFiles, cancellationToken);

                if (entry.Kind == FileEntryKind.File && !vanillaFiles.Contains(relativePath))
                {
                    bytesDone += entry.Length;
                    filesDone++;

                    if (progress is not null && (stopwatch.ElapsedMilliseconds - lastReport >= 100 || filesDone == toHash.Count))
                    {
                        lastReport = stopwatch.ElapsedMilliseconds;
                        progress.Report(new BackupProgress("Checking backed-up files", relativePath, bytesDone, totalBytes, filesDone, toHash.Count));
                    }
                }

                files.Add(new TrackedFile(relativePath, status, entry.Length, entry.LastWriteTimeUtc));
            }

            var vanillaFolders = SnapshotService.GetVanillaDirectories(baselineSnapshot);
            var existingFolders = new HashSet<string>(SnapshotService.ListRealDirectories(rootPath, cancellationToken), StringComparer.OrdinalIgnoreCase);
            var folders = trackedFolders
                .Where(folder => !vanillaFolders.Contains(folder) && existingFolders.Contains(folder))
                .OrderByDescending(folder => folder.Count(character => character == '/'))
                .ThenBy(folder => folder, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new TrackedPurgePlan(rootPath, backups, files, folders, trackedFiles.Count - present.Count);
        }

        public async Task<TrackedPurgeResult> PurgeAsync(TrackedPurgePlan plan, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(plan);

            var planned = plan.ToRemove.ToList();
            var entries = planned.Count == 0
                ? new Dictionary<string, ScannedEntry>(StringComparer.OrdinalIgnoreCase)
                : (await Task.Run(() => _fileSystemProvider.ScanWithoutFollowingLinks(plan.RootPath, cancellationToken), cancellationToken))
                    .ToDictionary(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase);

            var kept = new List<string>();
            var toDelete = new List<ScannedEntry>();

            foreach (var file in planned)
            {
                // Only what the preview verified: a file that changed or became a link since then is kept.
                if (entries.TryGetValue(file.RelativePath, out var entry)
                    && entry.Kind == FileEntryKind.File
                    && entry.Length == file.Length
                    && entry.LastWriteTimeUtc == file.LastWriteTimeUtc)
                    toDelete.Add(entry);
                else if (entries.ContainsKey(file.RelativePath))
                    kept.Add(file.RelativePath);
            }

            var safetyBackupPath = await _safetyBackupService.CreateSafetyBackupAsync(
                toDelete.Select(entry => new ArchiveFileItem(entry.FullPath, entry.RelativePath)).ToList(),
                "pre-purge-tracked",
                "Purge",
                cancellationToken);

            var deleted = 0;

            foreach (var entry in toDelete)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    _fileSystemProvider.DeleteEntry(entry);
                    deleted++;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    kept.Add(entry.RelativePath);
                }
            }

            // Deepest first, so a folder whose subfolders were just removed is empty when its turn comes.
            var foldersRemoved = 0;
            var foldersKept = 0;

            foreach (var relative in plan.Folders)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directory = new DirectoryInfo(Path.Combine(plan.RootPath, relative.Replace('/', Path.DirectorySeparatorChar)));

                try
                {
                    if (!directory.Exists || SnapshotService.IsDirectoryLink(directory))
                        continue;

                    if (directory.EnumerateFileSystemInfos().Any())
                    {
                        foldersKept++;
                        continue;
                    }

                    directory.Delete(recursive: false);
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

            return new TrackedPurgeResult(plan, deleted, safetyBackupPath, foldersRemoved, foldersKept, kept);
        }

        private static TrackedFileStatus Classify(
            string rootPath,
            string relativePath,
            List<FileFingerprint> versions,
            ScannedEntry entry,
            HashSet<string> vanillaFiles,
            CancellationToken cancellationToken)
        {
            if (entry.Kind != FileEntryKind.File)
                return TrackedFileStatus.VortexLink;

            if (vanillaFiles.Contains(relativePath))
                return TrackedFileStatus.OriginalGameFile;

            var hashed = versions.Where(version => !string.IsNullOrEmpty(version.Sha256)).ToList();

            if (hashed.Count == 0)
                return TrackedFileStatus.CannotVerify;

            // A different size cannot be the same content; no need to read the file.
            if (hashed.All(version => version.Length != entry.Length))
                return TrackedFileStatus.ChangedSinceBackup;

            var current = SnapshotService.TryComputeHash(entry.FullPath, cancellationToken);

            if (current is null)
                return TrackedFileStatus.CannotVerify;

            return hashed.Any(version => string.Equals(version.Sha256, current, StringComparison.OrdinalIgnoreCase))
                ? TrackedFileStatus.Remove
                : TrackedFileStatus.ChangedSinceBackup;
        }

        /// <summary>
        /// Every file of the backups (relative to the game folder) with the versions recorded for it, and every folder the
        /// backups recorded. A path in both backups counts as ours when it matches either version.
        /// </summary>
        private static (Dictionary<string, List<FileFingerprint>> Files, HashSet<string> Folders) CollectTracked(IEnumerable<LocatedBackupManifest> backups)
        {
            var files = new Dictionary<string, List<FileFingerprint>>(StringComparer.OrdinalIgnoreCase);
            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var unit in backups.SelectMany(backup => backup.Manifest.Units).Where(unit => TrackedComponents.Contains(unit.Component)))
            {
                foreach (var fingerprint in unit.FileFingerprints)
                {
                    var relative = ToGamePath(unit, fingerprint.RelativePath, stripEntryPrefix: true);

                    if (relative.Length == 0)
                        continue;

                    if (!files.TryGetValue(relative, out var versions))
                        files[relative] = versions = [];

                    versions.Add(fingerprint);
                }

                foreach (var directory in unit.DirectoryFingerprints)
                {
                    var relative = ToGamePath(unit, directory.RelativePath, stripEntryPrefix: false);

                    if (relative.Length > 0)
                        folders.Add(relative);
                }
            }

            return (files, folders);
        }

        /// <summary>
        /// File fingerprints use archive entry names, which carry the unit's entry prefix in single-archive backups.
        /// </summary>
        private static string ToGamePath(BackupManifestUnit unit, string path, bool stripEntryPrefix)
        {
            var normalized = path.Replace('\\', '/');

            if (stripEntryPrefix && unit.EntryPrefix.Length > 0 && normalized.StartsWith(unit.EntryPrefix, StringComparison.OrdinalIgnoreCase))
                normalized = normalized[unit.EntryPrefix.Length..];

            var destination = unit.RelativeDestination.Replace('\\', '/').Trim('/');
            var combined = destination.Length == 0 ? normalized : $"{destination}/{normalized}";

            return combined.Trim('/');
        }
    }
}