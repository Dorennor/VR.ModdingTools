using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using UniversalBackupTool.Interface.Interfaces.Backups;
using UniversalBackupTool.Interface.Interfaces.FileSystem;
using UniversalBackupTool.Interface.Interfaces.Jobs;
using UniversalBackupTool.Interfaces.Snapshots;
using UniversalBackupTool.Interfaces.Storage;
using UniversalBackupTool.Model.Models.Backups;
using UniversalBackupTool.Model.Models.FileSystem;
using UniversalBackupTool.Model.Models.Jobs;
using UniversalBackupTool.Model.Models.Profiles;
using UniversalBackupTool.Model.Models.Settings;
using UniversalBackupTool.Model.Models.Snapshots;

namespace UniversalBackupTool.Service.Services.Backups
{
    public sealed class BackupJobRunner : IBackupJobRunner
    {
        private const string MonolithicArchiveName = "backup.zip";
        private const string RootFilesArchiveName = "_root_files";

        private static readonly JsonSerializerOptions ManifestJsonOptions = BackupLocations.ManifestJsonOptions;

        private readonly IArchiveService _archiveService;
        private readonly IFileSystemProvider _fileSystemProvider;
        private readonly ISnapshotService _snapshotService;
        private readonly ISnapshotStore _snapshotStore;
        private readonly IIgnorePatternMatcher _ignorePatternMatcher;
        private readonly IBackupJobStateService _backupJobStateService;
        private readonly IAppStoragePathsProvider _storagePathsProvider;

        public BackupJobRunner(
            IArchiveService archiveService,
            IFileSystemProvider fileSystemProvider,
            ISnapshotService snapshotService,
            ISnapshotStore snapshotStore,
            IIgnorePatternMatcher ignorePatternMatcher,
            IBackupJobStateService backupJobStateService,
            IAppStoragePathsProvider storagePathsProvider)
        {
            _archiveService = archiveService ?? throw new ArgumentNullException(nameof(archiveService));
            _fileSystemProvider = fileSystemProvider ?? throw new ArgumentNullException(nameof(fileSystemProvider));
            _snapshotService = snapshotService ?? throw new ArgumentNullException(nameof(snapshotService));
            _snapshotStore = snapshotStore ?? throw new ArgumentNullException(nameof(snapshotStore));
            _ignorePatternMatcher = ignorePatternMatcher ?? throw new ArgumentNullException(nameof(ignorePatternMatcher));
            _backupJobStateService = backupJobStateService ?? throw new ArgumentNullException(nameof(backupJobStateService));
            _storagePathsProvider = storagePathsProvider ?? throw new ArgumentNullException(nameof(storagePathsProvider));
        }

        public async Task<BackupBatchResult> RunBackupAsync(
            IReadOnlyList<BackupProfile> profiles,
            BackupOptions options,
            AppSettings settings,
            IProgress<BackupProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(profiles);
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(settings);

            if (profiles.Count == 0)
                throw new ArgumentException("Select at least one profile.", nameof(profiles));

            if (!options.HasAnyComponent)
                throw new ArgumentException("Select at least one component to back up.", nameof(options));

            if (options.UsesBackupDestination && string.IsNullOrWhiteSpace(settings.GoogleDriveBackupPath))
                throw new InvalidOperationException("The backup destination folder is not configured. Set it on the Settings page.");

            var results = new List<BackupResult>();
            var failedFiles = new List<FailedBackupFile>();
            var batchWarnings = new List<string>();
            var resultProfiles = new Dictionary<BackupResult, BackupProfile>(ReferenceEqualityComparer.Instance);

            // The Vortex AppData folder is shared by all games: it is backed up once per run, into its own folder, when
            // at least one selected game is managed by Vortex.
            var backupSharedAppData = options.IncludeVortexAppData && profiles.Any(profile => profile.UsesVortex);

            for (var index = 0; index < profiles.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var profile = profiles[index];
                var profilePrefix = profiles.Count > 1 ? $"{profile.GameName} ({index + 1}/{profiles.Count})" : profile.GameName;
                var profileResults = new List<BackupResult>();
                var skippedComponentWarnings = new List<string>();
                var componentList = ExpandPerComponentOptions(options, profile);

                // A full game backup that does not happen never stops the other games of the run.
                var fullGameNotRun = false;

                if (options.IncludeFullGame && !componentList.Any(component => component.IncludeFullGame))
                {
                    batchWarnings.Add($"\"{profile.GameName}\": no full game backup, because the profile has no export folder or no game folder. Set them on the Profiles page.");
                    fullGameNotRun = true;
                }

                if (componentList.Count == 0)
                {
                    // Only the shared Vortex AppData was selected: nothing to do per game.
                    if (!fullGameNotRun && !(backupSharedAppData && profile.UsesVortex))
                        batchWarnings.Add($"\"{profile.GameName}\" was skipped: it is not managed by Vortex, so the staging and Vortex AppData components do not apply to it.");

                    continue;
                }

                foreach (var componentOptions in componentList)
                {
                    var componentLabel = DescribeComponent(componentOptions);
                    var prefix = $"{profilePrefix} - {componentLabel}";

                    try
                    {
                        var result = await BackupProfileAsync(profile, componentOptions, settings, prefix, progress, cancellationToken);
                        profileResults.Add(result);
                    }
                    catch (InvalidOperationException exception) when (exception.Message.StartsWith("Nothing to back up for ", StringComparison.Ordinal))
                    {
                        skippedComponentWarnings.Add(exception.Message);
                    }
                    catch (FullGameBackupStoppedException exception)
                    {
                        batchWarnings.Add(exception.Message);
                        fullGameNotRun = true;
                    }
                }

                if (profileResults.Count == 0 && fullGameNotRun)
                {
                    batchWarnings.AddRange(skippedComponentWarnings);
                    continue;
                }

                if (profileResults.Count == 0)
                {
                    var message = skippedComponentWarnings.Count > 0
                        ? string.Join(" ", skippedComponentWarnings)
                        : $"Nothing to back up for \"{profile.GameName}\".";

                    // The shared Vortex AppData backup below still covers this Vortex game, as it did when AppData was
                    // part of each game's backup.
                    if (backupSharedAppData && profile.UsesVortex)
                    {
                        batchWarnings.Add(message);
                        continue;
                    }

                    throw new InvalidOperationException(message);
                }

                if (skippedComponentWarnings.Count > 0)
                    profileResults[0].Warnings.AddRange(skippedComponentWarnings);

                foreach (var result in profileResults)
                    resultProfiles[result] = profile;

                results.AddRange(profileResults);
                failedFiles.AddRange(profileResults.SelectMany(result => result.FailedFiles));
            }

            if (backupSharedAppData)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var sharedProfile = VortexAppDataBackup.CreateProfile();

                try
                {
                    var result = await BackupProfileAsync(
                        sharedProfile,
                        new BackupOptions(false, false, true, false, options.Monolithic),
                        settings,
                        VortexAppDataBackup.DisplayName,
                        progress,
                        cancellationToken);

                    resultProfiles[result] = sharedProfile;
                    results.Add(result);
                    failedFiles.AddRange(result.FailedFiles);
                }
                catch (InvalidOperationException exception) when (exception.Message.StartsWith("Nothing to back up for ", StringComparison.Ordinal))
                {
                    batchWarnings.Add(exception.Message);
                }
            }

            if (results.Count == 0)
                throw new InvalidOperationException(string.Join(" ", batchWarnings.DefaultIfEmpty("Nothing to back up.")));

            if (failedFiles.Count > 0)
            {
                var firstFailed = results.First(result => result.FailedFiles.Count > 0);
                var profile = resultProfiles[firstFailed];
                var rootPath = VortexAppDataBackup.IsSharedBackup(profile.Id)
                    ? settings.VortexAppDataPath
                    : profile.GameRootPath ?? profile.VortexStagingPath ?? string.Empty;

                var job = ActiveBackupJob.Create(profile.Id, firstFailed.BackupDirectory, rootPath) with
                {
                    FailedFiles = failedFiles,
                    FailedPaths = failedFiles.Select(file => file.SourcePath).ToList(),
                    PendingManifestPaths = results.Where(result => result.FailedFiles.Count > 0 && result.ManifestPath is not null)
                        .Select(result => result.ManifestPath!)
                        .ToList(),
                    PendingArchiveMoves = results.SelectMany(result => result.PendingArchiveMoves).ToList()
                };

                await _backupJobStateService.SaveAsync(job, cancellationToken);
            }

            return new BackupBatchResult(results, failedFiles, results.Sum(result => result.FilesArchived)) { Warnings = batchWarnings };
        }

        /// <summary>
        /// One option set per selected per-game component that applies to the profile: staging only applies to games
        /// managed by Vortex. Vortex AppData is not a per-game component: <see cref="RunBackupAsync"/> backs it up once.
        /// </summary>
        private static List<BackupOptions> ExpandPerComponentOptions(BackupOptions options, BackupProfile profile)
        {
            var expanded = new List<BackupOptions>();

            if (options.IncludeStaging && profile.UsesVortex)
                expanded.Add(new BackupOptions(true, false, false, false, options.Monolithic));

            if (options.IncludeGameConfigs)
                expanded.Add(new BackupOptions(false, true, false, false, options.Monolithic));

            if (options.IncludeGameAdditions)
                expanded.Add(new BackupOptions(false, false, false, false, options.Monolithic, IncludeGameAdditions: true));

            if (options.IncludeGameChanges)
                expanded.Add(new BackupOptions(false, false, false, false, options.Monolithic, IncludeGameChanges: true));

            if (options.IncludeCustomFolders)
                expanded.Add(new BackupOptions(false, false, false, true, options.Monolithic));

            if (options.IncludeFullGame && profile.GetFullGameExportFolder() is not null && !string.IsNullOrWhiteSpace(profile.GameRootPath))
                expanded.Add(new BackupOptions(false, false, false, false, IncludeFullGame: true));

            return expanded;
        }

        private static string DescribeComponent(BackupOptions options)
        {
            if (options.IncludeStaging)
                return "Staging";

            if (options.IncludeGameConfigs)
                return "Configs";

            if (options.IncludeGameAdditions)
                return "New game files";

            if (options.IncludeGameChanges)
                return "Changed game files";

            if (options.IncludeVortexAppData)
                return "Vortex AppData";

            if (options.IncludeCustomFolders)
                return "Custom paths";

            if (options.IncludeFullGame)
                return "Full game";

            return "Unknown";
        }

        public async Task<BackupBatchResult> ResumeBackupAsync(
            ActiveBackupJob job,
            AppSettings settings,
            IProgress<BackupProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(job);
            ArgumentNullException.ThrowIfNull(settings);

            var groups = job.FailedFiles
                .GroupBy(file => file.ArchivePath, StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var archivePath = group.Key;

                    if (!File.Exists(archivePath))
                    {
                        var publishedArchive = job.PendingArchiveMoves.FirstOrDefault(move =>
                            string.Equals(move.TemporaryPath, archivePath, StringComparison.OrdinalIgnoreCase))?.FinalPath;

                        if (publishedArchive is null || !File.Exists(publishedArchive))
                            throw new InvalidOperationException($"The archive of the paused job no longer exists: {archivePath}. Discard the paused job.");

                        archivePath = publishedArchive;
                    }

                    return new FailedArchiveGroup(archivePath, group.ToList());
                })
                .ToList();

            var totalFiles = job.FailedFiles.Count;
            var totalBytes = job.FailedFiles.Sum(file => GetLength(file.SourcePath));
            var tracker = new ProgressTracker("Retrying locked files", totalBytes, totalFiles, progress);

            var remaining = new List<FailedBackupFile>();
            var completedHashes = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            var archived = 0;

            foreach (var group in groups)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var items = group.Files.Select(file => new ArchiveFileItem(file.SourcePath, file.EntryName)).ToList();
                var result = await _archiveService.AppendToArchiveAsync(group.ArchivePath, items, settings, tracker, cancellationToken);

                archived += result.FilesWritten;
                remaining.AddRange(result.Failed.Select(item => new FailedBackupFile(group.ArchivePath, item.EntryName, item.SourcePath)));

                if (!completedHashes.TryGetValue(group.ArchivePath, out var archiveHashes))
                {
                    archiveHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    completedHashes.Add(group.ArchivePath, archiveHashes);
                }

                foreach (var file in group.Files)
                {
                    if (result.FileHashes.TryGetValue(file.SourcePath, out var hash))
                        archiveHashes[file.EntryName.Replace('\\', '/')] = hash;
                }
            }

            tracker.Finish();

            var paths = _storagePathsProvider.GetPaths();

            if (remaining.Count == 0)
            {
                foreach (var move in job.PendingArchiveMoves)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Directory.CreateDirectory(Path.GetDirectoryName(move.FinalPath)!);

                    if (File.Exists(move.TemporaryPath))
                    {
                        if (File.Exists(move.FinalPath))
                            File.Delete(move.TemporaryPath);
                        else
                            File.Move(move.TemporaryPath, move.FinalPath);
                    }
                    else if (!File.Exists(move.FinalPath))
                    {
                        throw new FileNotFoundException("A completed archive temporary file is missing.", move.TemporaryPath);
                    }
                }

                foreach (var manifestPath in job.PendingManifestPaths)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await MarkManifestCompleteAsync(manifestPath, job.PendingArchiveMoves, completedHashes, cancellationToken);
                }

                await _backupJobStateService.DeleteAsync(paths.ActiveBackupJobFilePath, cancellationToken);
            }
            else
            {
                await _backupJobStateService.SaveAsync(
                    job with { FailedFiles = remaining, FailedPaths = remaining.Select(file => file.SourcePath).ToList() },
                    cancellationToken);
            }

            return new BackupBatchResult([], remaining, archived);
        }

        public IReadOnlyList<ExistingFullGameArchive> FindExistingFullGameArchives(IReadOnlyList<BackupProfile> profiles)
        {
            ArgumentNullException.ThrowIfNull(profiles);

            var archives = new List<ExistingFullGameArchive>();

            foreach (var profile in profiles)
            {
                if (profile.GetFullGameExportFolder() is not { } exportFolder || string.IsNullOrWhiteSpace(profile.GameRootPath))
                    continue;

                try
                {
                    var archive = new FileInfo(BackupLocations.GetFullGameArchivePath(exportFolder, profile));

                    if (archive.Exists)
                        archives.Add(new ExistingFullGameArchive(profile.GameName, archive.FullName, archive.Length));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    // An unusable export folder stops that game's backup later, before anything is deleted.
                }
            }

            return archives;
        }

        /// <summary>
        /// Writes the whole game folder as one zip into the profile's export folder. The old archive is deleted before the
        /// new one is started, never overwritten: on a cloud drive, replacing a file makes the client download it first.
        /// Everything that can be checked happens before that delete, so a wrong path, an empty folder or a locked file
        /// leaves the old archive alone. Links are read through, so the archive holds a working game without any links.
        /// </summary>
        private async Task<BackupResult> BackupFullGameAsync(
            BackupProfile profile,
            AppSettings settings,
            string progressLabel,
            IProgress<BackupProgress>? progress,
            CancellationToken cancellationToken)
        {
            var rootPath = profile.GameRootPath!;
            var exportFolder = profile.GetFullGameExportFolder()!;

            if (!Directory.Exists(rootPath))
                throw new FullGameBackupStoppedException($"\"{profile.GameName}\": no full game backup, the game folder was not found: {rootPath}");

            if (IsSameOrInside(exportFolder, rootPath))
                throw new FullGameBackupStoppedException($"\"{profile.GameName}\": no full game backup, the export folder is inside the game folder, so the archive would end up in itself: {exportFolder}");

            try
            {
                Directory.CreateDirectory(exportFolder);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                throw new FullGameBackupStoppedException($"\"{profile.GameName}\": no full game backup, the export folder cannot be used: {exportFolder} ({exception.Message})");
            }

            progress?.Report(new BackupProgress($"{progressLabel}: scanning files", null, 0, 0, 0, 0));

            var warnings = new List<string>();
            var filter = new CountingIgnoreFilter(_ignorePatternMatcher.Create(profile.UseIgnoreList ? settings.GlobalIgnorePatterns : []));
            var scan = await Task.Run(() => _fileSystemProvider.ScanFollowingLinks(rootPath, cancellationToken), cancellationToken);

            warnings.AddRange(scan.Warnings);

            var realFiles = scan.Files
                .Where(file => !filter.IsIgnored(file.RelativePath))
                .Select(file => new ArchiveFileItem(file.FullPath, file.RelativePath))
                .ToList();

            if (realFiles.Count == 0)
                throw new FullGameBackupStoppedException($"\"{profile.GameName}\": no full game backup, there are no files to archive in the game folder: {rootPath}");

            // Without these entries an unpacked game would miss folders it may expect to exist.
            var items = realFiles
                .Concat(scan.EmptyDirectories
                    .Where(directory => !filter.IsIgnored(directory))
                    .Select(directory => new ArchiveFileItem(Path.Combine(rootPath, directory.Replace('/', Path.DirectorySeparatorChar)), directory + "/")))
                .ToList();

            var lockedFiles = await Task.Run(() => FindLockedFiles(realFiles, $"{progressLabel}: checking for locked files", progress, cancellationToken), cancellationToken);

            if (lockedFiles.Count > 0)
            {
                throw new FullGameBackupStoppedException(
                    $"\"{profile.GameName}\": no full game backup and the old archive was not touched: {lockedFiles.Count:N0} file(s) are in use or access is denied. Close the game and launchers and run it again. For example: {string.Join(", ", lockedFiles.Take(10))}");
            }

            var archivePath = BackupLocations.GetFullGameArchivePath(exportFolder, profile);

            DeletePreviousFullGameArchive(archivePath, profile, progressLabel, progress);

            var temporaryPath = archivePath + $".{Guid.NewGuid():N}.pending";
            var unit = new UnitPlan(new BackupManifestUnit(BackupComponent.FullGame, Path.GetFileName(archivePath), string.Empty, rootPath, string.Empty), items);
            var archiveSettings = ApplyComponentSizeRule(settings, [unit], profile, BackupComponent.FullGame, warnings);
            var totalBytes = realFiles.Sum(file => GetLength(file.SourcePath));
            var tracker = new ProgressTracker($"{progressLabel}: archiving", totalBytes, items.Count, progress);

            try
            {
                var written = await _archiveService.CreateArchiveAsync(temporaryPath, items, archiveSettings, tracker, cancellationToken);
                var failedFiles = written.Failed.Select(item => new FailedBackupFile(temporaryPath, item.EntryName, item.SourcePath)).ToList();
                var pendingMoves = new List<PendingArchiveMove>();

                tracker.Finish();

                // Locked files keep the archive under its temporary name until Continue Backup has added them.
                if (failedFiles.Count > 0)
                    pendingMoves.Add(new PendingArchiveMove(temporaryPath, archivePath));
                else
                    File.Move(temporaryPath, archivePath);

                return new BackupResult(profile.GameName, exportFolder, written.FilesWritten, warnings, failedFiles)
                {
                    FullGameArchivePath = archivePath,
                    PendingArchiveMoves = pendingMoves,
                    IgnoredFiles = filter.IgnoredCount,
                    IgnoredExamples = filter.Examples.ToList()
                };
            }
            catch (Exception exception)
            {
                TryDeleteFile(temporaryPath);

                if (exception is not (IOException or UnauthorizedAccessException))
                    throw;

                // Not turned into a skipped game: when the disk is full, the next game would lose its old archive as well.
                throw new IOException($"\"{profile.GameName}\": the full game backup failed and the old archive is already deleted, so this game has no archive now: {exception.Message}", exception);
            }
        }

        /// <summary>
        /// Files that cannot be opened the way archiving opens them. Opening does not read any data.
        /// </summary>
        private static List<string> FindLockedFiles(
            IReadOnlyList<ArchiveFileItem> files,
            string phase,
            IProgress<BackupProgress>? progress,
            CancellationToken cancellationToken)
        {
            var locked = new List<string>();

            for (var index = 0; index < files.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (index % 500 == 0)
                    progress?.Report(new BackupProgress(phase, null, 0, 0, index, files.Count));

                try
                {
                    using var handle = File.OpenHandle(files[index].SourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                }
                catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
                {
                    // Gone since the scan; archiving reports it the same way.
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    locked.Add(files[index].SourcePath);
                }
            }

            return locked;
        }

        /// <summary>
        /// Deletes the old archive and the temporary files of runs that never finished. If the old archive cannot be
        /// deleted the game is skipped, because writing over it is exactly what must not happen.
        /// </summary>
        private static void DeletePreviousFullGameArchive(string archivePath, BackupProfile profile, string progressLabel, IProgress<BackupProgress>? progress)
        {
            try
            {
                if (File.Exists(archivePath))
                {
                    File.Delete(archivePath);
                    progress?.Report(new BackupProgress($"{progressLabel}: deleted the old archive", archivePath, 0, 0, 0, 0));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new FullGameBackupStoppedException($"\"{profile.GameName}\": no full game backup, the old archive could not be deleted: {archivePath} ({exception.Message})");
            }

            foreach (var leftover in Directory.EnumerateFiles(Path.GetDirectoryName(archivePath)!, Path.GetFileName(archivePath) + ".*.pending").ToList())
                TryDeleteFile(leftover);
        }

        private static bool IsSameOrInside(string path, string folder)
        {
            try
            {
                var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) + Path.DirectorySeparatorChar;
                var fullFolder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;

                return fullPath.StartsWith(fullFolder, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return false;
            }
        }

        private Task<BackupResult> BackupProfileAsync(
            BackupProfile profile,
            BackupOptions options,
            AppSettings settings,
            string progressLabel,
            IProgress<BackupProgress>? progress,
            CancellationToken cancellationToken)
        {
            if (options.IncludeFullGame)
                return BackupFullGameAsync(profile, settings, progressLabel, progress, cancellationToken);

            return options.Monolithic
                ? BackupLegacyProfileAsync(profile, options, settings, progressLabel, progress, cancellationToken)
                : BackupIncrementalProfileAsync(profile, options, settings, progressLabel, progress, cancellationToken);
        }

        private async Task<BackupResult> BackupIncrementalProfileAsync(
            BackupProfile profile,
            BackupOptions options,
            AppSettings settings,
            string progressLabel,
            IProgress<BackupProgress>? progress,
            CancellationToken cancellationToken)
        {
            var warnings = new List<string>();
            var filter = new CountingIgnoreFilter(_ignorePatternMatcher.Create(profile.UseIgnoreList ? settings.GlobalIgnorePatterns : []));

            progress?.Report(new BackupProgress($"{progressLabel}: scanning files", null, 0, 0, 0, 0));
            var units = await CollectUnitsAsync(profile, options, settings, filter, warnings, PrefixedProgress.Wrap(progressLabel, progress), cancellationToken);

            if (units.Count == 0)
            {
                var reason = warnings.Count > 0 ? " " + string.Join(" ", warnings) : string.Empty;
                throw new InvalidOperationException($"Nothing to back up for \"{profile.GameName}\".{reason}");
            }

            var component = units[0].Manifest.Component;
            var componentRootDirectory = GetComponentRootDirectory(settings.GoogleDriveBackupPath, profile, component);
            var previousManifest = await LoadLatestCompleteManifestAsync(componentRootDirectory, profile, cancellationToken);
            var previousUnits = previousManifest?.Manifest.Units ?? [];
            var archiveRoot = Path.Combine(componentRootDirectory, "_archives");
            var plans = new List<ArchivePlan>();

            foreach (var unit in units)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var files = CollectFileMetadata(unit.Files);
                var directories = CollectDirectoryMetadata(unit.Manifest);
                var prior = previousUnits.FirstOrDefault(candidate =>
                    string.Equals(GetUnitKey(candidate), GetUnitKey(unit.Manifest), StringComparison.OrdinalIgnoreCase));

                if (prior is not null && FileMetadataMatches(prior.FileFingerprints, files) && DirectoryMetadataMatches(prior.DirectoryFingerprints, directories))
                {
                    var cachedFiles = files.Select(file => file with
                    {
                        Sha256 = prior.FileFingerprints.First(previous => string.Equals(previous.RelativePath, file.RelativePath, StringComparison.OrdinalIgnoreCase)).Sha256
                    }).ToList();
                    var priorArchivePath = ResolveArchivePath(previousManifest!.Directory, prior);

                    if (cachedFiles.All(file => file.Sha256.Length > 0) && File.Exists(priorArchivePath))
                    {
                        plans.Add(new ArchivePlan(unit, cachedFiles, directories, prior, prior.ArchiveFile, priorArchivePath, prior.ArchiveRootRelativePath, true));
                        continue;
                    }
                }

                var (archiveRelativePath, finalArchivePath) = CreateArchivePath(archiveRoot, unit.Manifest);
                plans.Add(new ArchivePlan(unit, files, directories, prior, archiveRelativePath, finalArchivePath, "..", false));
            }

            var failedFiles = new List<FailedBackupFile>();
            var pendingMoves = new List<PendingArchiveMove>();
            var temporaryArchives = new List<string>();
            var manifestUnits = new List<BackupManifestUnit>();
            var filesToArchive = 0;
            var writePlans = plans.Where(plan => !plan.IsPreviousReuse).ToList();
            var totalBytes = writePlans.Sum(plan => plan.Unit.Files.Sum(file => GetLength(file.SourcePath)));
            var tracker = new ProgressTracker($"{progressLabel}: archiving changes", totalBytes, writePlans.Sum(plan => plan.Unit.Files.Count), progress);
            var modsArchived = 0;
            var modsReused = 0;
            var archiveSettings = ApplyComponentSizeRule(settings, units, profile, component, warnings);
            var backupDirectory = CreateBackupDirectory(componentRootDirectory);

            // Each archive to write gets its temporary path up front, so they can be written in parallel and cleaned up
            // together if anything fails.
            var temporaryPaths = new string?[plans.Count];
            var archiveResults = new ArchiveWriteResult?[plans.Count];

            for (var index = 0; index < plans.Count; index++)
            {
                if (!plans[index].IsPreviousReuse)
                {
                    temporaryPaths[index] = plans[index].FinalArchivePath + $".{Guid.NewGuid():N}.pending";
                    temporaryArchives.Add(temporaryPaths[index]!);
                }
            }

            try
            {
                Directory.CreateDirectory(archiveRoot);

                // Compression runs on one core per archive, so several archives (mods) are written at the same time.
                // The largest start first, so a big mod does not end up running alone at the end.
                var writeOrder = Enumerable.Range(0, plans.Count)
                    .Where(index => !plans[index].IsPreviousReuse)
                    .OrderByDescending(index => plans[index].Unit.Files.Sum(file => GetLength(file.SourcePath)))
                    .ToList();
                var parallelOptions = new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Max(1, settings.EffectiveParallelArchives),
                    CancellationToken = cancellationToken
                };

                await Parallel.ForEachAsync(writeOrder, parallelOptions, async (index, token) =>
                {
                    archiveResults[index] = await _archiveService.CreateArchiveAsync(temporaryPaths[index]!, plans[index].Unit.Files, archiveSettings, tracker, token);
                });

                for (var index = 0; index < plans.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var plan = plans[index];
                    var unit = plan.Unit;

                    if (unit.Manifest.Component == BackupComponent.StagingFolders && plan.IsPreviousReuse)
                        modsReused++;

                    if (plan.IsPreviousReuse)
                    {
                        manifestUnits.Add(unit.Manifest with
                        {
                            ArchiveFile = plan.ArchiveRelativePath,
                            Fingerprint = ComputeUnitFingerprint(plan.Files, plan.Directories),
                            ArchiveRootRelativePath = plan.ArchiveRootRelativePath,
                            FileFingerprints = plan.Files,
                            DirectoryFingerprints = plan.Directories
                        });
                        continue;
                    }

                    var temporaryPath = temporaryPaths[index]!;
                    var archiveResult = archiveResults[index]!;
                    var filesWithHashes = AttachHashes(plan.Files, unit.Files, archiveResult.FileHashes);
                    var hashesMatchPrevious = plan.PreviousUnit is not null
                        && FileContentsMatch(plan.PreviousUnit.FileFingerprints, filesWithHashes)
                        && DirectoryMetadataMatches(plan.PreviousUnit.DirectoryFingerprints, plan.Directories);

                    if (archiveResult.Failed.Count > 0)
                    {
                        filesToArchive += archiveResult.FilesWritten;
                        failedFiles.AddRange(archiveResult.Failed.Select(item => new FailedBackupFile(temporaryPath, item.EntryName, item.SourcePath)));
                        pendingMoves.Add(new PendingArchiveMove(temporaryPath, plan.FinalArchivePath));
                    }
                    else if (hashesMatchPrevious && plan.PreviousUnit is not null && previousManifest is not null)
                    {
                        TryDeleteFile(temporaryPath);
                        temporaryArchives.Remove(temporaryPath);
                        if (unit.Manifest.Component == BackupComponent.StagingFolders)
                            modsReused++;
                        manifestUnits.Add(unit.Manifest with
                        {
                            ArchiveFile = plan.PreviousUnit.ArchiveFile,
                            Fingerprint = ComputeUnitFingerprint(filesWithHashes, plan.Directories),
                            ArchiveRootRelativePath = plan.PreviousUnit.ArchiveRootRelativePath,
                            FileFingerprints = filesWithHashes,
                            DirectoryFingerprints = plan.Directories
                        });
                        continue;
                    }
                    else
                    {
                        filesToArchive += archiveResult.FilesWritten;

                        if (!FileMetadataMatches(plan.Files, CollectFileMetadata(unit.Files))
                            || !DirectoryMetadataMatches(plan.Directories, CollectDirectoryMetadata(unit.Manifest)))
                            throw new IOException($"Files or folders changed while their backup archive was being created: {unit.Manifest.RelativeDestination}");

                        if (File.Exists(plan.FinalArchivePath))
                            throw new IOException($"The destination archive already exists unexpectedly: {plan.FinalArchivePath}");

                        File.Move(temporaryPath, plan.FinalArchivePath);
                        temporaryArchives.Remove(temporaryPath);
                    }

                    if (unit.Manifest.Component == BackupComponent.StagingFolders)
                        modsArchived++;

                    manifestUnits.Add(unit.Manifest with
                    {
                        ArchiveFile = plan.ArchiveRelativePath,
                        Fingerprint = ComputeUnitFingerprint(filesWithHashes, plan.Directories),
                        ArchiveRootRelativePath = plan.ArchiveRootRelativePath,
                        FileFingerprints = filesWithHashes,
                        DirectoryFingerprints = plan.Directories
                    });
                }

                tracker.Finish();
                var currentModNames = units
                    .Where(unit => unit.Manifest.Component == BackupComponent.StagingFolders)
                    .Select(unit => unit.Manifest.RelativeDestination)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var previousModNames = previousUnits
                    .Where(unit => unit.Component == BackupComponent.StagingFolders)
                    .Select(unit => unit.RelativeDestination)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var removedMods = options.IncludeStaging ? previousModNames.Except(currentModNames, StringComparer.OrdinalIgnoreCase).Count() : 0;

                var manifest = new BackupManifest
                {
                    Version = 2,
                    IsComplete = failedFiles.Count == 0,
                    ProfileId = profile.Id,
                    GameName = profile.GameName,
                    CreatedAt = DateTimeOffset.Now,
                    IsMonolithic = false,
                    Units = manifestUnits
                };

                await WriteManifestAsync(backupDirectory, manifest, cancellationToken);

                if (failedFiles.Count == 0)
                    await CleanupRetainedBackupsAsync(backupDirectory, settings.MaxPreservedBackupsPerGame, warnings, cancellationToken);

                return new BackupResult(profile.GameName, backupDirectory, filesToArchive, warnings, failedFiles)
                {
                    ManifestPath = Path.Combine(backupDirectory, BackupManifest.FileName),
                    ModsArchived = modsArchived,
                    ModsReused = modsReused,
                    ModsRemoved = removedMods,
                    PendingArchiveMoves = pendingMoves,
                    IgnoredFiles = filter.IgnoredCount,
                    IgnoredExamples = filter.Examples.ToList()
                };
            }
            catch
            {
                foreach (var temporaryPath in temporaryArchives)
                    TryDeleteFile(temporaryPath);

                TryDeleteDirectory(backupDirectory);
                throw;
            }
        }

        private async Task<BackupResult> BackupLegacyProfileAsync(
            BackupProfile profile,
            BackupOptions options,
            AppSettings settings,
            string progressLabel,
            IProgress<BackupProgress>? progress,
            CancellationToken cancellationToken)
        {
            var warnings = new List<string>();
            var filter = _ignorePatternMatcher.Create(profile.UseIgnoreList ? settings.GlobalIgnorePatterns : []);

            progress?.Report(new BackupProgress($"{progressLabel}: scanning files", null, 0, 0, 0, 0));

            var units = await CollectUnitsAsync(profile, options, settings, filter, warnings, PrefixedProgress.Wrap(progressLabel, progress), cancellationToken);

            if (units.Count == 0)
            {
                var reason = warnings.Count > 0 ? " " + string.Join(" ", warnings) : string.Empty;
                throw new InvalidOperationException($"Nothing to back up for \"{profile.GameName}\".{reason}");
            }

            var component = units[0].Manifest.Component;
            var componentRootDirectory = GetComponentRootDirectory(settings.GoogleDriveBackupPath, profile, component);
            var archiveSettings = ApplyComponentSizeRule(settings, units, profile, component, warnings);
            var backupDirectory = CreateBackupDirectory(componentRootDirectory);

            try
            {
                var failedFiles = new List<FailedBackupFile>();
                var totalFiles = units.Sum(unit => unit.Files.Count);
                var totalBytes = units.Sum(unit => unit.Files.Sum(file => GetLength(file.SourcePath)));
                var tracker = new ProgressTracker($"{progressLabel}: archiving", totalBytes, totalFiles, progress);
                var archived = 0;

                foreach (var archiveGroup in units.GroupBy(unit => unit.Manifest.ArchiveFile, StringComparer.OrdinalIgnoreCase))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var archivePath = Path.Combine(backupDirectory, archiveGroup.Key.Replace('/', Path.DirectorySeparatorChar));
                    var items = archiveGroup.SelectMany(unit => unit.Files).ToList();
                    var result = await _archiveService.CreateArchiveAsync(archivePath, items, archiveSettings, tracker, cancellationToken);

                    archived += result.FilesWritten;
                    failedFiles.AddRange(result.Failed.Select(item => new FailedBackupFile(archivePath, item.EntryName, item.SourcePath)));
                }

                tracker.Finish();

                var manifest = new BackupManifest
                {
                    ProfileId = profile.Id,
                    GameName = profile.GameName,
                    CreatedAt = DateTimeOffset.Now,
                    IsMonolithic = options.Monolithic,
                    IsComplete = failedFiles.Count == 0,
                    Units = units.Select(unit => unit.Manifest).ToList()
                };

                await WriteManifestAsync(backupDirectory, manifest, cancellationToken);

                if (failedFiles.Count == 0)
                    await CleanupRetainedBackupsAsync(backupDirectory, settings.MaxPreservedBackupsPerGame, warnings, cancellationToken);

                return new BackupResult(profile.GameName, backupDirectory, archived, warnings, failedFiles)
                {
                    ManifestPath = Path.Combine(backupDirectory, BackupManifest.FileName)
                };
            }
            catch
            {
                TryDeleteDirectory(backupDirectory);
                throw;
            }
        }

        /// <summary>
        /// Staging, Vortex AppData and custom paths are backed up completely. The ignore list only filters the game folder
        /// components (Game configs and Changed game files): the game folder holds Vortex's own files (deployment manifests,
        /// folder markers) next to the real leftovers and configs worth keeping.
        /// </summary>
        private async Task<List<UnitPlan>> CollectUnitsAsync(
            BackupProfile profile,
            BackupOptions options,
            AppSettings settings,
            IIgnoreFilter configFilter,
            List<string> warnings,
            IProgress<BackupProgress>? progress,
            CancellationToken cancellationToken)
        {
            var units = new List<UnitPlan>();

            if (options.IncludeStaging)
            {
                var staging = await CollectFolderAsync(profile.VortexStagingPath, "Vortex staging folder", warnings, cancellationToken);

                if (staging is not null && staging.Count > 0)
                {
                    if (options.Monolithic)
                        units.Add(CreateMonolithicUnit(BackupComponent.StagingFolders, "staging/", profile.VortexStagingPath!, string.Empty, staging));
                    else
                        units.AddRange(CreateStagingUnits(profile.VortexStagingPath!, staging));
                }
            }

            if (options.IncludeGameConfigs)
            {
                var configs = await CollectGameFolderChangesAsync(profile, GameFolderScope.Configs, "Game configs", configFilter, warnings, progress, cancellationToken);

                if (configs is not null && configs.Count > 0)
                    units.Add(CreateSingleUnit(options, BackupComponent.GameConfigs, "configs", "configs.zip", profile.GameRootPath!, configs));
            }

            if (options.IncludeGameAdditions)
            {
                var additions = await CollectGameFolderChangesAsync(profile, GameFolderScope.Additions, "New game files", configFilter, warnings, progress, cancellationToken);

                if (additions is not null && additions.Count > 0)
                    units.Add(CreateSingleUnit(options, BackupComponent.GameAdditions, "additions", "game_additions.zip", profile.GameRootPath!, additions));
            }

            if (options.IncludeGameChanges)
            {
                var changed = await CollectGameFolderChangesAsync(profile, GameFolderScope.Changes, "Changed game files", configFilter, warnings, progress, cancellationToken);

                if (changed is not null && changed.Count > 0)
                    units.Add(CreateSingleUnit(options, BackupComponent.GameChanges, "changes", "game_changes.zip", profile.GameRootPath!, changed));
            }

            if (options.IncludeVortexAppData)
            {
                // One folder for all games, set in the settings.
                var appDataPath = settings.VortexAppDataPath;

                if (string.IsNullOrWhiteSpace(appDataPath))
                    warnings.Add("The Vortex AppData folder is not set. Set it on the Settings page.");

                var appData = await CollectFolderAsync(appDataPath, "Vortex AppData folder", warnings, cancellationToken);

                if (appData is not null && appData.Count > 0)
                    units.Add(CreateSingleUnit(options, BackupComponent.VortexAppData, "appdata", "vortex_appdata.zip", appDataPath, appData));
            }

            if (options.IncludeCustomFolders)
            {
                var customIndex = 0;

                // Each custom path is a folder or a single file and becomes its own unit (own archive, reused when unchanged).
                foreach (var customPath in profile.CustomFolders.Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => path.Trim()))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    string rootPath;
                    List<SourceFile>? files;

                    if (File.Exists(customPath))
                    {
                        var fullPath = Path.GetFullPath(customPath);
                        var fileName = Path.GetFileName(fullPath);

                        rootPath = Path.GetDirectoryName(fullPath)!;
                        files = [new SourceFile(fullPath, fileName)];
                    }
                    else if (Directory.Exists(customPath))
                    {
                        rootPath = customPath;
                        files = await CollectFolderAsync(customPath, "Custom folder", warnings, cancellationToken);
                    }
                    else
                    {
                        warnings.Add($"Custom path was not found and was skipped: {customPath}");
                        continue;
                    }

                    if (files is null || files.Count == 0)
                        continue;

                    customIndex++;

                    var key = $"custom{customIndex}_{SanitizeFileName(GetLeafName(customPath))}";
                    var unit = CreateSingleUnit(options, BackupComponent.CustomFolders, key, $"custom/{key}.zip", rootPath, files);

                    units.Add(unit with { Manifest = unit.Manifest with { CustomPath = customPath } });
                }
            }

            return units;
        }

        /// <summary>
        /// Every file of the folder; the ignore list does not apply here (see <see cref="CollectUnitsAsync"/>).
        /// </summary>
        private async Task<List<SourceFile>?> CollectFolderAsync(
            string? rootPath,
            string label,
            List<string> warnings,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(rootPath))
                return null;

            if (!Directory.Exists(rootPath))
            {
                warnings.Add($"{label} was not found and was skipped: {rootPath}");
                return null;
            }

            // Backups follow links so the real contents are archived (a restore on another PC gets real files).
            var scan = await Task.Run(() => _fileSystemProvider.ScanFollowingLinks(rootPath, cancellationToken), cancellationToken);
            warnings.AddRange(scan.Warnings);

            return scan.Files
                .Select(file => new SourceFile(file.FullPath, file.RelativePath))
                .ToList();
        }

        /// <summary>
        /// Which differences from the vanilla snapshot a game folder component takes.
        /// </summary>
        private enum GameFolderScope
        {
            /// <summary>
            /// Real files the snapshot does not list (the purge preview's list). Links are left out.
            /// </summary>
            Configs,

            /// <summary>
            /// Configs plus every linked entry (Vortex deployments, also links placed over vanilla files), archived with
            /// their real content. Judged by metadata only: no file is read, so a reinstall that gives every vanilla file
            /// a new timestamp costs nothing.
            /// </summary>
            Additions,

            /// <summary>
            /// Additions plus vanilla files whose content differs from the snapshot. Vanilla files with a new timestamp but
            /// the same size are hashed once; identical ones get their new timestamp recorded in the snapshot.
            /// </summary>
            Changes
        }

        /// <summary>
        /// Game folder files that differ from the baseline snapshot, as selected by <paramref name="scope"/>. A reinstall
        /// or file verification rewrites every vanilla file with new timestamps, which must never turn the whole game into
        /// configs or additions.
        /// </summary>
        private async Task<List<SourceFile>?> CollectGameFolderChangesAsync(
            BackupProfile profile,
            GameFolderScope scope,
            string label,
            IIgnoreFilter filter,
            List<string> warnings,
            IProgress<BackupProgress>? progress,
            CancellationToken cancellationToken)
        {
            var rootPath = profile.GameRootPath;

            if (string.IsNullOrWhiteSpace(rootPath))
                return null;

            if (!Directory.Exists(rootPath))
            {
                warnings.Add($"Game root folder was not found and was skipped: {rootPath}");
                return null;
            }

            var baseline = await _snapshotStore.LoadAsync(_storagePathsProvider.GetPaths().SnapshotsDirectory, profile.Id, cancellationToken);

            if (baseline is null)
            {
                warnings.Add($"{label} were skipped: no baseline snapshot exists. Create one on the Vanilla & Purge page.");
                return null;
            }

            var includeModified = scope == GameFolderScope.Changes;
            var includeLinks = scope != GameFolderScope.Configs;
            var comparison = includeModified
                ? await CompareWithContentCheckAsync(profile, rootPath, baseline.Data, label, warnings, progress, cancellationToken)
                : await Task.Run(() => _snapshotService.CompareAsync(rootPath, baseline.Data, verifyContent: false, cancellationToken), cancellationToken);

            var changes = comparison.Changes
                .Where(change => scope switch
                {
                    GameFolderScope.Changes => true,
                    // New paths, plus links placed over vanilla files (a mod replacing a game file). Vanilla files that
                    // are still real files are never taken, whatever their timestamp or size.
                    GameFolderScope.Additions => !baseline.Data.Files.ContainsKey(change.Key) || change.Value.IsLink,
                    _ => !baseline.Data.Files.ContainsKey(change.Key)
                })
                .Select(change => change.Value)
                .ToList();

            var files = new List<SourceFile>();

            foreach (var change in changes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var relative = change.RelativePath.Replace('\\', '/');
                var fullPath = Path.Combine(rootPath, relative.Replace('/', Path.DirectorySeparatorChar));

                if (change.Kind == FileEntryKind.DirectoryLink)
                {
                    // New game files and Changed game files are complete copies, so a linked folder (Vortex junction or
                    // symlink) is archived with its real contents. Game configs leave links out: they are mod
                    // deployments, whose data the staging backup holds.
                    if (!includeLinks || !Directory.Exists(fullPath))
                        continue;

                    var scan = await Task.Run(() => _fileSystemProvider.ScanFollowingLinks(fullPath, cancellationToken), cancellationToken);
                    warnings.AddRange(scan.Warnings.Select(warning => $"{label}: {warning}"));
                    files.AddRange(scan.Files.Select(file => new SourceFile(file.FullPath, relative + "/" + file.RelativePath)));
                    continue;
                }

                // Changes also contain files deleted since the snapshot, which there is nothing to archive for. Linked
                // files (hardlinks, symlinks) are read through the link, so their real content is archived.
                if ((change.IsLink && !includeLinks) || !File.Exists(fullPath))
                    continue;

                files.Add(new SourceFile(fullPath, relative));
            }

            if (!includeLinks)
            {
                var linkedChanges = changes.Count(change => change.IsLink);

                if (linkedChanges > 0)
                    warnings.Add($"{label}: {linkedChanges:N0} linked entr(ies) in the game folder (Vortex deployments) were left out of the configs; their data is part of the staging backup. New game files and Changed game files include them.");
            }

            return files
                .Where(file => !filter.IsIgnored(file.RelativePath))
                .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Comparison that hashes vanilla files whose timestamp changed but whose size did not. Files found identical get
        /// their new timestamp written to the snapshot, also when the backup is cancelled halfway, so no hashing work is
        /// lost and the next run only reads what is left.
        /// </summary>
        private async Task<SnapshotComparison> CompareWithContentCheckAsync(
            BackupProfile profile,
            string rootPath,
            SnapshotData baseline,
            string label,
            List<string> warnings,
            IProgress<BackupProgress>? progress,
            CancellationToken cancellationToken)
        {
            var snapshotsDirectory = _storagePathsProvider.GetPaths().SnapshotsDirectory;
            var reanchored = new List<FileMetadata>();
            var saved = false;

            try
            {
                var comparison = await Task.Run(
                    () => _snapshotService.CompareAsync(rootPath, baseline, verifyContent: true, cancellationToken, progress, file =>
                    {
                        lock (reanchored)
                            reanchored.Add(file);
                    }),
                    cancellationToken);

                if (comparison.Reanchored.Count > 0)
                {
                    await _snapshotStore.UpdateEntriesAsync(snapshotsDirectory, profile.Id, comparison.Reanchored, cancellationToken);
                    saved = true;
                    warnings.Add($"{label}: {comparison.Reanchored.Count:N0} vanilla file(s) had new timestamps but the same content as in the snapshot; they were not backed up and the snapshot now has their new timestamps.");
                }

                if (comparison.UnverifiedFiles > 0)
                    warnings.Add($"{label}: {comparison.UnverifiedFiles:N0} file(s) with new timestamps were backed up without a content check because the snapshot has no hashes for them. Recreate the snapshot on the Vanilla & Purge page (while the game folder is vanilla) to enable content checks.");

                if (comparison.UnreadableFiles > 0)
                    warnings.Add($"{label}: {comparison.UnreadableFiles:N0} file(s) with new timestamps could not be read for a content check (in use or access denied), so they count as changed. Close the game and launchers and run the backup again.");

                if (comparison.VersionChanged > 0)
                    warnings.Add($"{label}: {comparison.VersionChanged:N0} file(s) have another file version than in the snapshot, so they count as changed. If the game was updated, the snapshot is out of date: follow the update steps with Purge tracked files and create a new snapshot.");

                if (comparison.Relocated.Count > 0)
                {
                    warnings.Add($"{label}: {comparison.Relocated.Count:N0} file(s) at new paths have the content of a vanilla file (moved or copied). They are backed up, because a mod can rely on the new path:");
                    warnings.AddRange(comparison.Relocated.Take(10).Select(file => $"    {file.RelativePath} = {file.OriginalPath}{(file.OriginalMissing ? " (moved)" : " (copy)")}"));
                }

                return comparison;
            }
            finally
            {
                List<FileMetadata> pending;

                lock (reanchored)
                    pending = saved ? [] : [.. reanchored];

                if (pending.Count > 0)
                {
                    try
                    {
                        // Cancelled or failed while hashing: keep what was already verified.
                        await _snapshotStore.UpdateEntriesAsync(snapshotsDirectory, profile.Id, pending, CancellationToken.None);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                    }
                }
            }
        }

        private static IEnumerable<UnitPlan> CreateStagingUnits(string stagingRoot, List<SourceFile> files)
        {
            // One archive per top-level mod folder, so a single mod can be restored on its own.
            foreach (var group in files.GroupBy(file => GetTopLevelFolder(file.RelativePath), StringComparer.OrdinalIgnoreCase))
            {
                var modName = group.Key;
                var archiveFile = modName.Length == 0
                    ? $"staging/{RootFilesArchiveName}.zip"
                    : $"staging/{SanitizeFileName(modName)}.zip";

                var entries = group
                    .Select(file => new ArchiveFileItem(file.FullPath, modName.Length == 0 ? file.RelativePath : file.RelativePath[(modName.Length + 1)..]))
                    .ToList();

                yield return new UnitPlan(
                    new BackupManifestUnit(BackupComponent.StagingFolders, archiveFile, string.Empty, stagingRoot, modName),
                    entries);
            }
        }

        private static UnitPlan CreateSingleUnit(
            BackupOptions options,
            BackupComponent component,
            string monolithicKey,
            string archiveFile,
            string rootPath,
            List<SourceFile> files)
        {
            return options.Monolithic
                ? CreateMonolithicUnit(component, monolithicKey + "/", rootPath, string.Empty, files)
                : new UnitPlan(
                    new BackupManifestUnit(component, archiveFile, string.Empty, rootPath, string.Empty),
                    files.Select(file => new ArchiveFileItem(file.FullPath, file.RelativePath)).ToList());
        }

        private static UnitPlan CreateMonolithicUnit(
            BackupComponent component,
            string entryPrefix,
            string rootPath,
            string relativeDestination,
            List<SourceFile> files)
        {
            return new UnitPlan(
                new BackupManifestUnit(component, MonolithicArchiveName, entryPrefix, rootPath, relativeDestination),
                files.Select(file => new ArchiveFileItem(file.FullPath, entryPrefix + file.RelativePath)).ToList());
        }

        private static string GetTopLevelFolder(string relativePath)
        {
            var separatorIndex = relativePath.IndexOf('/');

            return separatorIndex < 0 ? string.Empty : relativePath[..separatorIndex];
        }

        private static async Task<LocatedManifest?> LoadLatestCompleteManifestAsync(
            string gameDirectory,
            BackupProfile profile,
            CancellationToken cancellationToken)
        {
            var latest = await BackupLocations.FindLatestCompleteManifestAsync(gameDirectory, profile, cancellationToken);

            return latest is null ? null : new LocatedManifest(latest.Directory, latest.Manifest);
        }

        /// <summary>
        /// Keeps the newest <paramref name="maxPreservedBackups"/> complete snapshots of a component folder and deletes older
        /// ones plus archives no retained manifest references. Incomplete snapshots never use a retention slot: the ones still
        /// awaiting Continue Backup are kept, abandoned ones (job discarded) are deleted because they cannot be restored.
        /// Folders without a manifest are never touched; an unreadable manifest keeps its folder and disables archive deletion.
        /// </summary>
        private async Task CleanupRetainedBackupsAsync(
            string currentBackupDirectory,
            int maxPreservedBackups,
            List<string> warnings,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(warnings);

            if (maxPreservedBackups < 1)
                return;

            var gameDirectory = Path.GetDirectoryName(currentBackupDirectory);

            if (string.IsNullOrWhiteSpace(gameDirectory) || !Directory.Exists(gameDirectory))
                return;

            var snapshotDirectories = Directory.EnumerateDirectories(gameDirectory)
                .Where(directory => !string.Equals(Path.GetFileName(directory), "_archives", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (snapshotDirectories.Count <= maxPreservedBackups)
                return;

            var pausedSnapshots = await LoadPausedSnapshotDirectoriesAsync(warnings, cancellationToken);
            var manifests = new List<LocatedManifest>();
            var unreadable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var directory in snapshotDirectories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var manifestPath = Path.Combine(directory, BackupManifest.FileName);

                if (!File.Exists(manifestPath))
                    continue;

                try
                {
                    await using var stream = File.OpenRead(manifestPath);
                    var manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(stream, ManifestJsonOptions, cancellationToken);

                    if (manifest?.Units is null)
                    {
                        unreadable.Add(directory);
                        continue;
                    }

                    manifests.Add(new LocatedManifest(directory, manifest));
                }
                catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
                {
                    unreadable.Add(directory);
                }
            }

            foreach (var directory in unreadable)
                warnings.Add($"Backup retention kept a snapshot with an unreadable manifest and did not remove unused archives: {directory}");

            // A null set means the paused job could not be read, so every incomplete snapshot is treated as paused.
            bool IsPaused(LocatedManifest item) => pausedSnapshots is null || pausedSnapshots.Contains(NormalizeDirectory(item.Directory));

            var retained = manifests
                .Where(item => item.Manifest.IsComplete)
                .OrderByDescending(item => item.Manifest.CreatedAt)
                .Take(maxPreservedBackups)
                .Select(item => item.Directory)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            retained.Add(currentBackupDirectory);

            foreach (var item in manifests.Where(item => !item.Manifest.IsComplete && IsPaused(item)))
                retained.Add(item.Directory);

            var toDelete = manifests
                .Where(item => !retained.Contains(item.Directory))
                .Select(item => item.Directory)
                .ToList();

            if (toDelete.Count == 0)
                return;

            var referencedArchives = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in manifests.Where(item => retained.Contains(item.Directory)))
            {
                cancellationToken.ThrowIfCancellationRequested();

                foreach (var unit in item.Manifest.Units)
                {
                    try
                    {
                        referencedArchives.Add(ResolveArchivePath(item.Directory, unit));
                    }
                    catch (InvalidDataException)
                    {
                        warnings.Add($"Backup retention skipped an invalid archive path in snapshot: {item.Directory}");
                    }
                }
            }

            foreach (var directory in toDelete)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    Directory.Delete(directory, recursive: true);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    warnings.Add($"Backup retention could not remove old snapshot: {directory}");
                }
            }

            var archiveDirectory = Path.Combine(gameDirectory, "_archives");

            if (unreadable.Count > 0 || !Directory.Exists(archiveDirectory))
                return;

            foreach (var archiveFile in Directory.EnumerateFiles(archiveDirectory, "*.zip", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (referencedArchives.Contains(Path.GetFullPath(archiveFile)))
                    continue;

                try
                {
                    File.Delete(archiveFile);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    warnings.Add($"Backup retention could not remove unused archive: {archiveFile}");
                }
            }
        }

        /// <summary>
        /// Snapshot folders whose manifests are waiting for Continue Backup. Returns null when the job state cannot be read.
        /// </summary>
        private async Task<HashSet<string>?> LoadPausedSnapshotDirectoriesAsync(List<string> warnings, CancellationToken cancellationToken)
        {
            ActiveBackupJob? job;

            try
            {
                job = await _backupJobStateService.LoadAsync(_storagePathsProvider.GetPaths().ActiveBackupJobFilePath, cancellationToken);
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
            {
                warnings.Add("Backup retention could not read the paused backup job, so incomplete snapshots were kept.");
                return null;
            }

            var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (job is null || job.IsCompleted)
                return directories;

            foreach (var manifestPath in job.PendingManifestPaths)
            {
                var directory = Path.GetDirectoryName(manifestPath);

                if (!string.IsNullOrWhiteSpace(directory))
                    directories.Add(NormalizeDirectory(directory));
            }

            return directories;
        }

        private static string NormalizeDirectory(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

        private static string ResolveArchivePath(string manifestDirectory, BackupManifestUnit unit)
        {
            if (unit.ArchiveRootRelativePath is not "." and not "..")
                throw new InvalidDataException("The backup manifest contains an unsupported archive root.");

            var archiveRoot = Path.GetFullPath(Path.Combine(manifestDirectory, unit.ArchiveRootRelativePath));
            var rootPrefix = Path.TrimEndingDirectorySeparator(archiveRoot) + Path.DirectorySeparatorChar;
            var archivePath = Path.GetFullPath(Path.Combine(archiveRoot, unit.ArchiveFile.Replace('/', Path.DirectorySeparatorChar)));

            if (!archivePath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Archive path escapes its snapshot storage root: {unit.ArchiveFile}");

            return archivePath;
        }

        private static string GetUnitKey(BackupManifestUnit unit)
        {
            // The custom path itself (folder or file) identifies the unit; older manifests only have the folder root,
            // which equals the custom path for folders, so their archives keep being reused.
            var customRoot = unit.Component == BackupComponent.CustomFolders
                ? Path.GetFullPath(string.IsNullOrEmpty(unit.CustomPath) ? unit.OriginalRootPath : unit.CustomPath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                : string.Empty;

            return $"{unit.Component}|{unit.RelativeDestination}|{customRoot}";
        }

        private static List<FileFingerprint> CollectFileMetadata(IReadOnlyList<ArchiveFileItem> files)
        {
            return files.Select(file =>
            {
                var info = ResolveFileForMetadata(file.SourcePath);

                return new FileFingerprint(
                    file.EntryName.Replace('\\', '/'),
                    info.Exists ? info.Length : -1,
                    info.Exists ? info.CreationTimeUtc.Ticks : -1,
                    info.Exists ? info.LastWriteTimeUtc.Ticks : -1,
                    info.Exists ? info.Attributes : 0,
                    string.Empty);
            }).OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        /// For a symbolic link the target's size and times are what change when the content changes; the link's own
        /// metadata stays the same, which would make an edited target look unchanged.
        /// </summary>
        private static FileInfo ResolveFileForMetadata(string path)
        {
            var info = new FileInfo(path);
            info.Refresh();

            try
            {
                if (info.Exists && info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is FileInfo { Exists: true } target)
                    return target;
            }
            catch (IOException)
            {
            }

            return info;
        }

        private static bool IsSingleFileUnit(BackupManifestUnit unit)
        {
            if (unit.Component != BackupComponent.CustomFolders || string.IsNullOrEmpty(unit.CustomPath))
                return false;

            var customPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(unit.CustomPath));
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(unit.OriginalRootPath));

            return !string.Equals(customPath, root, StringComparison.OrdinalIgnoreCase);
        }

        private static List<DirectoryFingerprint> CollectDirectoryMetadata(BackupManifestUnit unit)
        {
            // A single custom file has no folder structure of its own; its parent folder is not part of the backup.
            if (IsSingleFileUnit(unit))
                return [];

            var root = unit.Component == BackupComponent.StagingFolders && unit.RelativeDestination.Length > 0
                ? Path.Combine(unit.OriginalRootPath, unit.RelativeDestination)
                : unit.OriginalRootPath;

            if (!Directory.Exists(root))
                return [];

            var results = new List<DirectoryFingerprint>();
            var pending = new Stack<string>();
            pending.Push(root);

            while (pending.TryPop(out var current))
            {
                foreach (var directory in Directory.EnumerateDirectories(current))
                {
                    var info = new DirectoryInfo(directory);
                    info.Refresh();
                    var attributes = info.Attributes;

                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        continue;

                    var relative = Path.GetRelativePath(root, directory).Replace('\\', '/');
                    results.Add(new DirectoryFingerprint(relative, info.CreationTimeUtc.Ticks, info.LastWriteTimeUtc.Ticks, attributes));
                    pending.Push(directory);
                }
            }

            return results.OrderBy(directory => directory.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static bool FileMetadataMatches(IReadOnlyList<FileFingerprint> previous, IReadOnlyList<FileFingerprint> current)
        {
            if (previous.Count != current.Count)
                return false;

            var previousByPath = previous.ToDictionary(file => file.RelativePath, StringComparer.OrdinalIgnoreCase);
            return current.All(file => previousByPath.TryGetValue(file.RelativePath, out var old)
                && old.Length == file.Length
                && old.CreationTimeUtcTicks == file.CreationTimeUtcTicks
                && old.LastWriteTimeUtcTicks == file.LastWriteTimeUtcTicks
                && old.Attributes == file.Attributes);
        }

        private static bool DirectoryMetadataMatches(IReadOnlyList<DirectoryFingerprint> previous, IReadOnlyList<DirectoryFingerprint> current)
        {
            if (previous.Count != current.Count)
                return false;

            var previousByPath = previous.ToDictionary(directory => directory.RelativePath, StringComparer.OrdinalIgnoreCase);
            return current.All(directory => previousByPath.TryGetValue(directory.RelativePath, out var old)
                && old.Attributes == directory.Attributes);
        }

        private static bool FileContentsMatch(IReadOnlyList<FileFingerprint> previous, IReadOnlyList<FileFingerprint> current)
        {
            if (previous.Count != current.Count)
                return false;

            var previousByPath = previous.ToDictionary(file => file.RelativePath, StringComparer.OrdinalIgnoreCase);
            return current.All(file => file.Sha256.Length > 0
                && previousByPath.TryGetValue(file.RelativePath, out var old)
                && old.Sha256.Length > 0
                && string.Equals(old.Sha256, file.Sha256, StringComparison.OrdinalIgnoreCase));
        }

        private static List<FileFingerprint> AttachHashes(
            IReadOnlyList<FileFingerprint> metadata,
            IReadOnlyList<ArchiveFileItem> files,
            IReadOnlyDictionary<string, string> hashes)
        {
            var sourceByEntry = files.ToDictionary(file => file.EntryName.Replace('\\', '/'), file => file.SourcePath, StringComparer.OrdinalIgnoreCase);
            return metadata.Select(file => file with
            {
                Sha256 = sourceByEntry.TryGetValue(file.RelativePath, out var sourcePath) && hashes.TryGetValue(sourcePath, out var hash)
                    ? hash
                    : string.Empty
            }).ToList();
        }

        private static string ComputeUnitFingerprint(IReadOnlyList<FileFingerprint> files, IReadOnlyList<DirectoryFingerprint> directories)
        {
            var builder = new StringBuilder();

            foreach (var file in files.OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase))
            {
                builder.Append("F|").Append(file.RelativePath).Append('|').Append(file.Length).Append('|')
                    .Append(file.CreationTimeUtcTicks).Append('|').Append(file.LastWriteTimeUtcTicks).Append('|')
                    .Append((int)file.Attributes).Append('|').Append(file.Sha256).Append('\n');
            }

            foreach (var directory in directories.OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase))
            {
                builder.Append("D|").Append(directory.RelativePath).Append('|').Append(directory.CreationTimeUtcTicks).Append('|')
                    .Append(directory.LastWriteTimeUtcTicks).Append('|').Append((int)directory.Attributes).Append('\n');
            }

            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
        }

        private static async Task MarkManifestCompleteAsync(
            string manifestPath,
            IReadOnlyList<PendingArchiveMove> pendingMoves,
            IReadOnlyDictionary<string, Dictionary<string, string>> completedHashes,
            CancellationToken cancellationToken)
        {
            BackupManifest manifest;

            await using (var stream = File.OpenRead(manifestPath))
            {
                manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(stream, ManifestJsonOptions, cancellationToken)
                    ?? throw new InvalidDataException($"Backup manifest could not be read: {manifestPath}");
            }

            var snapshotDirectory = Path.GetDirectoryName(manifestPath)!;
            var units = manifest.Units.Select(unit =>
            {
                var archivePath = ResolveArchivePath(snapshotDirectory, unit);
                var finalPath = pendingMoves.FirstOrDefault(move => string.Equals(move.FinalPath, archivePath, StringComparison.OrdinalIgnoreCase))?.FinalPath;

                if (finalPath is null || !completedHashes.TryGetValue(finalPath, out var hashes))
                    return unit;

                var files = unit.FileFingerprints.Select(file => hashes.TryGetValue(file.RelativePath, out var hash)
                    ? file with { Sha256 = hash }
                    : file).ToList();

                return unit with
                {
                    FileFingerprints = files,
                    Fingerprint = ComputeUnitFingerprint(files, unit.DirectoryFingerprints)
                };
            }).ToList();

            await WriteManifestFileAsync(manifestPath, manifest with { IsComplete = true, Units = units }, cancellationToken);
        }

        private static string CreateBackupDirectory(string backupRoot)
        {
            var baseName = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            var candidate = Path.Combine(backupRoot, baseName);
            var suffix = 1;

            while (Directory.Exists(candidate))
            {
                suffix++;
                candidate = Path.Combine(backupRoot, $"{baseName}_{suffix}");
            }

            Directory.CreateDirectory(candidate);

            return candidate;
        }

        /// <summary>
        /// The settings the archives of one component are written with: when all its source files together (for staging
        /// the whole modpack, not only the mods that changed) are larger than
        /// <see cref="AppSettings.StoreComponentsLargerThanBytes"/>, every file is stored without compression. The total
        /// of the source folder keeps the decision stable from run to run. Archives reused from earlier runs keep whatever
        /// compression they have.
        /// </summary>
        private static AppSettings ApplyComponentSizeRule(
            AppSettings settings,
            IReadOnlyList<UnitPlan> units,
            BackupProfile profile,
            BackupComponent component,
            List<string> warnings)
        {
            if (settings.StoreComponentsLargerThanBytes <= 0)
                return settings;

            var componentBytes = units.Sum(unit => unit.Files.Sum(file => GetLength(file.SourcePath)));

            if (componentBytes <= settings.StoreComponentsLargerThanBytes)
                return settings;

            var name = VortexAppDataBackup.IsSharedBackup(profile.Id) ? VortexAppDataBackup.DisplayName : $"{profile.GameName} ({BackupLocations.GetComponentFolderName(component)})";
            warnings.Add(FormattableString.Invariant(
                $"{name}: {componentBytes / 1073741824.0:0.0} GB is above the {settings.StoreComponentsLargerThanBytes / 1073741824.0:0.##} GB limit in Settings, so new archives were stored without compression."));

            return settings with { CompressionRules = [new CompressionRule(long.MaxValue, System.IO.Compression.CompressionLevel.NoCompression)] };
        }

        /// <summary>
        /// The component folder (see <see cref="BackupLocations.GetComponentDirectory"/>), created when missing.
        /// </summary>
        private static string GetComponentRootDirectory(string backupRoot, BackupProfile profile, BackupComponent component)
        {
            var componentDirectory = BackupLocations.GetComponentDirectory(backupRoot, profile, component);
            Directory.CreateDirectory(componentDirectory);

            return componentDirectory;
        }

        private static Task WriteManifestAsync(string backupDirectory, BackupManifest manifest, CancellationToken cancellationToken)
        {
            var manifestPath = Path.Combine(backupDirectory, BackupManifest.FileName);
            return WriteManifestFileAsync(manifestPath, manifest, cancellationToken);
        }

        private static async Task WriteManifestFileAsync(string manifestPath, BackupManifest manifest, CancellationToken cancellationToken)
        {
            var temporaryPath = manifestPath + ".tmp";

            try
            {
                await using (var stream = File.Create(temporaryPath))
                    await JsonSerializer.SerializeAsync(stream, manifest, ManifestJsonOptions, cancellationToken);

                File.Move(temporaryPath, manifestPath, overwrite: true);
            }
            catch
            {
                TryDeleteFile(temporaryPath);
                throw;
            }
        }

        private static string SanitizeFileName(string name) => BackupLocations.SanitizeFileName(name);

        private static string GetLeafName(string path)
        {
            return Path.GetFileName(path.TrimEnd('\\', '/'));
        }

        private static (string RelativePath, string FullPath) CreateArchivePath(string archiveRoot, BackupManifestUnit unit)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(archiveRoot);
            ArgumentNullException.ThrowIfNull(unit);

            while (true)
            {
                var archiveIdentity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{GetUnitKey(unit)}|{Guid.NewGuid():N}")));
                var shortHash = archiveIdentity[..12];
                var friendlyName = BuildArchiveNamePrefix(unit);
                var fileName = $"{friendlyName}__{shortHash}.zip";
                var fullPath = Path.Combine(archiveRoot, fileName);

                if (!File.Exists(fullPath))
                    return ($"_archives/{fileName}", fullPath);
            }
        }

        private static string BuildArchiveNamePrefix(BackupManifestUnit unit)
        {
            ArgumentNullException.ThrowIfNull(unit);

            var baseName = unit.Component switch
            {
                BackupComponent.StagingFolders => string.IsNullOrWhiteSpace(unit.RelativeDestination)
                    ? RootFilesArchiveName
                    : unit.RelativeDestination,
                _ => Path.GetFileNameWithoutExtension(unit.ArchiveFile)
            };

            return SanitizeFileName(baseName);
        }

        private static long GetLength(string path)
        {
            try
            {
                // Archiving reads through a symbolic link, so the target's size is what gets written; the link itself is 0 bytes.
                return ResolveFileForMetadata(path).Length;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return 0;
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        private sealed record SourceFile(string FullPath, string RelativePath);

        /// <summary>
        /// A full game backup did not start for one game. Its old archive is untouched and the run goes on with the next.
        /// </summary>
        private sealed class FullGameBackupStoppedException(string message) : Exception(message);

        /// <summary>
        /// Prefixes progress phases of nested work (such as the snapshot content check) with the profile and component.
        /// </summary>
        private sealed class PrefixedProgress(string prefix, IProgress<BackupProgress> inner) : IProgress<BackupProgress>
        {
            public static IProgress<BackupProgress>? Wrap(string prefix, IProgress<BackupProgress>? inner) =>
                inner is null ? null : new PrefixedProgress(prefix, inner);

            public void Report(BackupProgress value) => inner.Report(value with { Phase = $"{prefix}: {value.Phase}" });
        }

        /// <summary>
        /// Counts what the ignore list skips, so each backup can report it (with a few examples) in Output.
        /// </summary>
        private sealed class CountingIgnoreFilter(IIgnoreFilter inner) : IIgnoreFilter
        {
            private const int MaxExamples = 5;

            private readonly List<string> _examples = [];

            public int IgnoredCount { get; private set; }

            public IReadOnlyList<string> Examples => _examples;

            public bool IsIgnored(string relativePath)
            {
                if (!inner.IsIgnored(relativePath))
                    return false;

                IgnoredCount++;

                if (_examples.Count < MaxExamples)
                    _examples.Add(relativePath);

                return true;
            }
        }

        private sealed record LocatedManifest(string Directory, BackupManifest Manifest);

        private sealed record FailedArchiveGroup(string ArchivePath, List<FailedBackupFile> Files);

        private sealed record UnitPlan(BackupManifestUnit Manifest, List<ArchiveFileItem> Files);

        private sealed record ArchivePlan(
            UnitPlan Unit,
            List<FileFingerprint> Files,
            List<DirectoryFingerprint> Directories,
            BackupManifestUnit? PreviousUnit,
            string ArchiveRelativePath,
            string FinalArchivePath,
            string ArchiveRootRelativePath,
            bool IsPreviousReuse);

        private sealed class ProgressTracker : IProgress<ArchiveProgress>
        {
            private readonly string _phase;
            private readonly long _totalBytes;
            private readonly int _totalFiles;
            private readonly IProgress<BackupProgress>? _target;
            private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

            private long _bytes;
            private int _files;
            private long _lastReportMilliseconds = -1000;
            private string? _current;

            public ProgressTracker(string phase, long totalBytes, int totalFiles, IProgress<BackupProgress>? target)
            {
                _phase = phase;
                _totalBytes = totalBytes;
                _totalFiles = totalFiles;
                _target = target;

                Report();
            }

            // Several archives are written at the same time and all report here.
            private readonly Lock _gate = new();

            public void Report(ArchiveProgress value)
            {
                lock (_gate)
                {
                    _bytes += value.BytesDelta;
                    _current = value.CurrentFile;

                    if (value.FileCompleted)
                        _files++;

                    // Large files report per buffer and many archives report at once, so throttle to keep the UI responsive.
                    if (_stopwatch.ElapsedMilliseconds - _lastReportMilliseconds >= 100)
                        Report();
                }
            }

            public void Finish()
            {
                _target?.Report(new BackupProgress(_phase, null, _totalBytes, _totalBytes, _totalFiles, _totalFiles));
            }

            private void Report()
            {
                _lastReportMilliseconds = _stopwatch.ElapsedMilliseconds;
                _target?.Report(new BackupProgress(_phase, _current, _bytes, _totalBytes, _files, _totalFiles));
            }
        }
    }
}