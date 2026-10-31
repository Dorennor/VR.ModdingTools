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

        public async Task<SnapshotData> CreateSnapshotAsync(Guid profileId, string rootPath, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(rootPath))
                throw new ArgumentException("Root path cannot be empty.", nameof(rootPath));

            if (!Directory.Exists(rootPath))
                return new SnapshotData(profileId, new Dictionary<string, FileMetadata>(StringComparer.OrdinalIgnoreCase));

            // Game folders are never scanned through links: links (Vortex deployments) are recorded as entries.
            var entries = await Task.Run(() => _fileSystemProvider.ScanWithoutFollowingLinks(rootPath, cancellationToken), cancellationToken);
            var dictionary = new Dictionary<string, FileMetadata>(StringComparer.OrdinalIgnoreCase);

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

        public Task<Dictionary<string, FileMetadata>> GetChangesAsync(string rootPath, SnapshotData baselineSnapshot, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(baselineSnapshot);

            var currentSnapshot = CreateSnapshotAsync(baselineSnapshot.ProfileId, rootPath, cancellationToken).GetAwaiter().GetResult();
            var changes = new Dictionary<string, FileMetadata>(StringComparer.OrdinalIgnoreCase);

            foreach (var current in currentSnapshot.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!baselineSnapshot.Files.TryGetValue(current.Key, out var baselineFile))
                {
                    changes[current.Key] = current.Value;
                    continue;
                }

                if (baselineFile.SizeBytes != current.Value.SizeBytes
                    || baselineFile.LastWriteTimeUtc != current.Value.LastWriteTimeUtc
                    || baselineFile.Kind != current.Value.Kind)
                    changes[current.Key] = current.Value;
            }

            foreach (var baseline in baselineSnapshot.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!currentSnapshot.Files.ContainsKey(baseline.Key))
                    changes[baseline.Key] = baseline.Value;
            }

            return Task.FromResult(changes);
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

            return new PurgeResult(deleted, safetyBackupPath) { LinksRemoved = linksRemoved };
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
    }
}