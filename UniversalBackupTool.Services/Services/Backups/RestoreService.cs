using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

using UniversalBackupTool.Interface.Interfaces.Backups;
using UniversalBackupTool.Model.Models.Backups;
using UniversalBackupTool.Model.Models.Profiles;

namespace UniversalBackupTool.Service.Services.Backups
{
    public sealed class RestoreService : IRestoreService
    {
        private static readonly JsonSerializerOptions ManifestJsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };

        private readonly ISafetyBackupService _safetyBackupService;

        public RestoreService(ISafetyBackupService safetyBackupService)
        {
            _safetyBackupService = safetyBackupService ?? throw new ArgumentNullException(nameof(safetyBackupService));
        }

        public async Task<BackupManifest?> ReadManifestAsync(string backupDirectory, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(backupDirectory))
                return null;

            var manifestPath = Path.Combine(backupDirectory, BackupManifest.FileName);

            if (!File.Exists(manifestPath))
                return null;

            try
            {
                await using var stream = File.OpenRead(manifestPath);
                var manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(stream, ManifestJsonOptions, cancellationToken);

                return manifest?.Units is null || !manifest.IsComplete ? null : manifest;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        public IReadOnlyList<RestorePlanItem> BuildPlan(
            BackupManifest manifest,
            IReadOnlyCollection<BackupProfile> profiles,
            IReadOnlyCollection<BackupComponent> components)
        {
            ArgumentNullException.ThrowIfNull(manifest);
            ArgumentNullException.ThrowIfNull(profiles);
            ArgumentNullException.ThrowIfNull(components);

            var profile = profiles.FirstOrDefault(candidate => candidate.Id == manifest.ProfileId)
                ?? profiles.FirstOrDefault(candidate => string.Equals(candidate.GameName, manifest.GameName, StringComparison.OrdinalIgnoreCase));

            var plan = new List<RestorePlanItem>();

            foreach (var unit in manifest.Units.Where(unit => components.Contains(unit.Component)))
            {
                var profilePath = ResolveProfilePath(profile, unit);
                var usesOriginal = string.IsNullOrWhiteSpace(profilePath);
                var root = usesOriginal ? unit.OriginalRootPath : profilePath!;

                var destination = string.IsNullOrEmpty(unit.RelativeDestination)
                    ? root
                    : Path.Combine(root, unit.RelativeDestination.Replace('/', Path.DirectorySeparatorChar));

                plan.Add(new RestorePlanItem(unit, root, destination, usesOriginal));
            }

            return plan;
        }

        public async Task<RestoreResult> RestoreAsync(
            string backupDirectory,
            IReadOnlyList<RestorePlanItem> plan,
            IProgress<BackupProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(backupDirectory))
                throw new ArgumentException("Backup directory cannot be empty.", nameof(backupDirectory));

            ArgumentNullException.ThrowIfNull(plan);

            progress?.Report(new BackupProgress("Reading backup", null, 0, 0, 0, 0));

            var items = await Task.Run(() => CollectExtractItems(backupDirectory, plan, cancellationToken), cancellationToken);

            var totalBytes = items.Sum(item => item.Length);
            var safetyBackupPath = await CreateSafetyBackupAsync(items, progress, cancellationToken);

            var failed = new List<string>();
            var restored = 0;
            long bytesDone = 0;

            foreach (var archiveGroup in items.GroupBy(item => item.ArchivePath, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();

                using var zipArchive = ZipFile.OpenRead(archiveGroup.Key);

                foreach (var item in archiveGroup)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    progress?.Report(new BackupProgress("Restoring", item.DestinationFile, bytesDone, totalBytes, restored + failed.Count, items.Count));

                    try
                    {
                        var entry = zipArchive.GetEntry(item.EntryName)
                            ?? throw new InvalidDataException($"Entry not found in archive: {item.EntryName}");

                        Directory.CreateDirectory(Path.GetDirectoryName(item.DestinationFile)!);
                        entry.ExtractToFile(item.DestinationFile, overwrite: true);

                        restored++;
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
                    {
                        failed.Add(item.DestinationFile);
                    }

                    bytesDone += item.Length;
                }
            }

            progress?.Report(new BackupProgress("Restoring", null, totalBytes, totalBytes, items.Count, items.Count));

            return new RestoreResult(restored, safetyBackupPath, failed);
        }

        private static string? ResolveProfilePath(BackupProfile? profile, BackupManifestUnit unit)
        {
            if (profile is null)
                return null;

            return unit.Component switch
            {
                BackupComponent.StagingFolders => profile.VortexStagingPath,
                BackupComponent.GameConfigs => profile.GameRootPath,
                BackupComponent.VortexAppData => profile.VortexAppDataPath,
                BackupComponent.CustomFolders => ResolveCustomRoot(profile, unit),
                _ => null
            };
        }

        /// <summary>
        /// Finds the profile's custom path for a unit: same full path, then the same path on another drive (restore
        /// drive swap), then the same name when exactly one entry has it. Returns the folder to extract into
        /// (the parent folder for a single-file entry), or null to fall back to the path stored in the backup.
        /// </summary>
        private static string? ResolveCustomRoot(BackupProfile profile, BackupManifestUnit unit)
        {
            var original = string.IsNullOrEmpty(unit.CustomPath) ? unit.OriginalRootPath : unit.CustomPath;
            var entries = profile.CustomFolders.Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => path.Trim()).ToList();

            var match = entries.FirstOrDefault(path => PathsEqual(path, original))
                ?? entries.FirstOrDefault(path => PathsEqual(WithoutDrive(path), WithoutDrive(original)));

            if (match is null)
            {
                var sameName = entries
                    .Where(path => string.Equals(GetLeafName(path), GetLeafName(original), StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (sameName.Count == 1)
                    match = sameName[0];
            }

            if (match is null)
                return null;

            return IsSingleFileUnit(unit) ? Path.GetDirectoryName(Path.GetFullPath(match)) : match;
        }

        private static bool IsSingleFileUnit(BackupManifestUnit unit)
        {
            return !string.IsNullOrEmpty(unit.CustomPath) && !PathsEqual(unit.CustomPath, unit.OriginalRootPath);
        }

        private static string WithoutDrive(string path)
        {
            return path.Length >= 2 && path[1] == ':' ? path[2..] : path;
        }

        private static bool PathsEqual(string left, string right)
        {
            static string Normalize(string path) => path.Replace('/', '\\').TrimEnd('\\');

            return string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
        }

        private static string GetLeafName(string path) => Path.GetFileName(path.TrimEnd('\\', '/'));

        private static List<ExtractItem> CollectExtractItems(string backupDirectory, IReadOnlyList<RestorePlanItem> plan, CancellationToken cancellationToken)
        {
            var items = new List<ExtractItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var planItem in plan)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (planItem.Unit.ArchiveRootRelativePath is not "." and not "..")
                    throw new InvalidDataException("The backup manifest contains an unsupported archive root.");

                var archiveRoot = Path.GetFullPath(Path.Combine(backupDirectory, planItem.Unit.ArchiveRootRelativePath));
                var archiveRootPrefix = Path.TrimEndingDirectorySeparator(archiveRoot) + Path.DirectorySeparatorChar;
                var archivePath = Path.GetFullPath(Path.Combine(archiveRoot, planItem.Unit.ArchiveFile.Replace('/', Path.DirectorySeparatorChar)));

                if (!archivePath.StartsWith(archiveRootPrefix, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Archive path escapes its snapshot storage root: {planItem.Unit.ArchiveFile}");

                if (!File.Exists(archivePath))
                    throw new FileNotFoundException($"Archive listed in the backup manifest is missing: {planItem.Unit.ArchiveFile}", archivePath);

                var destinationRoot = Path.GetFullPath(planItem.DestinationPath);
                var rootWithSeparator = Path.TrimEndingDirectorySeparator(destinationRoot) + Path.DirectorySeparatorChar;
                var prefix = planItem.Unit.EntryPrefix;

                using var zipArchive = ZipFile.OpenRead(archivePath);

                foreach (var entry in zipArchive.Entries)
                {
                    if (entry.FullName.EndsWith('/') || !entry.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var relative = entry.FullName[prefix.Length..].Replace('/', Path.DirectorySeparatorChar);
                    var destinationFile = Path.GetFullPath(Path.Combine(destinationRoot, relative));

                    // Reject entries such as "..\..\x" that would escape the destination folder.
                    if (!destinationFile.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"Archive entry points outside of the destination folder: {entry.FullName}");

                    if (seen.Add(archivePath + "|" + entry.FullName))
                        items.Add(new ExtractItem(archivePath, entry.FullName, destinationFile, entry.Length));
                }
            }

            return items;
        }

        private async Task<string?> CreateSafetyBackupAsync(IReadOnlyList<ExtractItem> items, IProgress<BackupProgress>? progress, CancellationToken cancellationToken)
        {
            var existing = items
                .Where(item => File.Exists(item.DestinationFile))
                .Select(item => new ArchiveFileItem(item.DestinationFile, ToSafetyEntryName(item.DestinationFile)))
                .DistinctBy(item => item.EntryName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (existing.Count == 0)
                return null;

            progress?.Report(new BackupProgress("Creating safety backup", null, 0, 0, 0, existing.Count));
            return await _safetyBackupService.CreateSafetyBackupAsync(existing, "pre-restore", "Restore", cancellationToken);
        }

        private static string ToSafetyEntryName(string fullPath)
        {
            return fullPath.Replace(":", string.Empty).Replace('\\', '/').TrimStart('/');
        }

        private sealed record ExtractItem(string ArchivePath, string EntryName, string DestinationFile, long Length);
    }
}