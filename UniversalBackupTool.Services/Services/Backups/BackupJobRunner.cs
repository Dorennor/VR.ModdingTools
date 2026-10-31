using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using UniversalBackupTool.Interface.Interfaces.Backups;
using UniversalBackupTool.Interface.Interfaces.FileSystem;
using UniversalBackupTool.Interface.Interfaces.Jobs;
using UniversalBackupTool.Interfaces.Snapshots;
using UniversalBackupTool.Interfaces.Storage;
using UniversalBackupTool.Model.Models.Backups;
using UniversalBackupTool.Model.Models.Jobs;
using UniversalBackupTool.Model.Models.Profiles;
using UniversalBackupTool.Model.Models.Settings;

namespace UniversalBackupTool.Service.Services.Backups
{
    public sealed class BackupJobRunner : IBackupJobRunner
    {
        private const string MonolithicArchiveName = "backup.zip";
        private const string RootFilesArchiveName = "_root_files";

        private static readonly JsonSerializerOptions ManifestJsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };

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

            if (string.IsNullOrWhiteSpace(settings.GoogleDriveBackupPath))
                throw new InvalidOperationException("The backup destination folder is not configured. Set it on the Settings page.");

            var results = new List<BackupResult>();
            var failedFiles = new List<FailedBackupFile>();

            for (var index = 0; index < profiles.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var profile = profiles[index];
                var profilePrefix = profiles.Count > 1 ? $"{profile.GameName} ({index + 1}/{profiles.Count})" : profile.GameName;
                var profileResults = new List<BackupResult>();
                var skippedComponentWarnings = new List<string>();

                foreach (var componentOptions in ExpandPerComponentOptions(options))
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
                }

                if (profileResults.Count == 0)
                {
                    var message = skippedComponentWarnings.Count > 0
                        ? string.Join(" ", skippedComponentWarnings)
                        : $"Nothing to back up for \"{profile.GameName}\".";
                    throw new InvalidOperationException(message);
                }

                if (skippedComponentWarnings.Count > 0)
                    profileResults[0].Warnings.AddRange(skippedComponentWarnings);

                results.AddRange(profileResults);
                failedFiles.AddRange(profileResults.SelectMany(result => result.FailedFiles));
            }

            if (failedFiles.Count > 0)
            {
                var firstFailed = results.First(result => result.FailedFiles.Count > 0);
                var profile = profiles.First(candidate => candidate.GameName == firstFailed.GameName);

                var job = ActiveBackupJob.Create(profile.Id, firstFailed.BackupDirectory, profile.GameRootPath ?? profile.VortexStagingPath ?? string.Empty) with
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

            return new BackupBatchResult(results, failedFiles, results.Sum(result => result.FilesArchived));
        }

        private static List<BackupOptions> ExpandPerComponentOptions(BackupOptions options)
        {
            var expanded = new List<BackupOptions>();

            if (options.IncludeStaging)
                expanded.Add(new BackupOptions(true, false, false, false, options.Monolithic));

            if (options.IncludeGameConfigs)
                expanded.Add(new BackupOptions(false, true, false, false, options.Monolithic));

            if (options.IncludeVortexAppData)
                expanded.Add(new BackupOptions(false, false, true, false, options.Monolithic));

            if (options.IncludeCustomFolders)
                expanded.Add(new BackupOptions(false, false, false, true, options.Monolithic));

            return expanded;
        }

        private static string DescribeComponent(BackupOptions options)
        {
            if (options.IncludeStaging)
                return "Staging";

            if (options.IncludeGameConfigs)
                return "Configs";

            if (options.IncludeVortexAppData)
                return "Vortex AppData";

            if (options.IncludeCustomFolders)
                return "Custom Folders";

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

        private Task<BackupResult> BackupProfileAsync(
            BackupProfile profile,
            BackupOptions options,
            AppSettings settings,
            string progressLabel,
            IProgress<BackupProgress>? progress,
            CancellationToken cancellationToken)
        {
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
            var filter = _ignorePatternMatcher.Create(profile.UseIgnoreList ? settings.GlobalIgnorePatterns : []);

            progress?.Report(new BackupProgress($"{progressLabel}: scanning files", null, 0, 0, 0, 0));
            var units = await CollectUnitsAsync(profile, options, settings, filter, warnings, cancellationToken);

            if (units.Count == 0)
            {
                var reason = warnings.Count > 0 ? " " + string.Join(" ", warnings) : string.Empty;
                throw new InvalidOperationException($"Nothing to back up for \"{profile.GameName}\".{reason}");
            }

            var component = units[0].Manifest.Component;
            var componentRootDirectory = GetComponentRootDirectory(settings.GoogleDriveBackupPath, profile.GameName, component);
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
            var tracker = new ProgressTracker($"{progressLabel}: checking and archiving changed files", totalBytes, writePlans.Sum(plan => plan.Unit.Files.Count), progress);
            var modsArchived = 0;
            var modsReused = 0;
            var backupDirectory = CreateBackupDirectory(componentRootDirectory);

            try
            {
                Directory.CreateDirectory(archiveRoot);

                foreach (var plan in plans)
                {
                    cancellationToken.ThrowIfCancellationRequested();
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

                    var temporaryPath = plan.FinalArchivePath + $".{Guid.NewGuid():N}.pending";
                    temporaryArchives.Add(temporaryPath);
                    var archiveResult = await _archiveService.CreateArchiveAsync(temporaryPath, unit.Files, settings, tracker, cancellationToken);
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
                    PendingArchiveMoves = pendingMoves
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

            var units = await CollectUnitsAsync(profile, options, settings, filter, warnings, cancellationToken);

            if (units.Count == 0)
            {
                var reason = warnings.Count > 0 ? " " + string.Join(" ", warnings) : string.Empty;
                throw new InvalidOperationException($"Nothing to back up for \"{profile.GameName}\".{reason}");
            }

            var component = units[0].Manifest.Component;
            var componentRootDirectory = GetComponentRootDirectory(settings.GoogleDriveBackupPath, profile.GameName, component);
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
                    var result = await _archiveService.CreateArchiveAsync(archivePath, items, settings, tracker, cancellationToken);

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

        private async Task<List<UnitPlan>> CollectUnitsAsync(
            BackupProfile profile,
            BackupOptions options,
            AppSettings settings,
            IIgnoreFilter filter,
            List<string> warnings,
            CancellationToken cancellationToken)
        {
            var units = new List<UnitPlan>();

            if (options.IncludeStaging)
            {
                var staging = await CollectFolderAsync(profile.VortexStagingPath, "Vortex staging folder", filter, warnings, cancellationToken);

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
                var configs = await CollectConfigFilesAsync(profile, filter, warnings, cancellationToken);

                if (configs is not null && configs.Count > 0)
                    units.Add(CreateSingleUnit(options, BackupComponent.GameConfigs, "configs", "configs.zip", profile.GameRootPath!, configs));
            }

            if (options.IncludeVortexAppData)
            {
                var appData = await CollectFolderAsync(profile.VortexAppDataPath, "Vortex AppData folder", filter, warnings, cancellationToken);

                if (appData is not null && appData.Count > 0)
                    units.Add(CreateSingleUnit(options, BackupComponent.VortexAppData, "appdata", "vortex_appdata.zip", profile.VortexAppDataPath!, appData));
            }

            if (options.IncludeCustomFolders)
            {
                var customIndex = 0;

                foreach (var folder in profile.CustomFolders)
                {
                    var files = await CollectFolderAsync(folder, "Custom folder", filter, warnings, cancellationToken);

                    if (files is null || files.Count == 0)
                        continue;

                    customIndex++;

                    var key = $"custom{customIndex}_{SanitizeFileName(GetLeafName(folder))}";

                    units.Add(CreateSingleUnit(options, BackupComponent.CustomFolders, key, $"custom/{key}.zip", folder, files));
                }
            }

            return units;
        }

        private async Task<List<SourceFile>?> CollectFolderAsync(
            string? rootPath,
            string label,
            IIgnoreFilter filter,
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

            var files = await _fileSystemProvider.EnumerateFilesAsync(rootPath, cancellationToken);

            return files
                .Select(file => new SourceFile(file, _fileSystemProvider.GetRelativePath(rootPath, file)))
                .Where(file => !filter.IsIgnored(file.RelativePath))
                .ToList();
        }

        private async Task<List<SourceFile>?> CollectConfigFilesAsync(
            BackupProfile profile,
            IIgnoreFilter filter,
            List<string> warnings,
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
                warnings.Add("Game configs were skipped: no baseline snapshot exists. Create one on the Vanilla & Purge page.");
                return null;
            }

            var changes = await Task.Run(() => _snapshotService.GetChangesAsync(rootPath, baseline.Data, cancellationToken), cancellationToken);

            // Changes also contain files deleted since the snapshot, which there is nothing to archive for.
            return changes.Keys
                .Select(relative => new SourceFile(Path.Combine(rootPath, relative.Replace('/', Path.DirectorySeparatorChar)), relative.Replace('\\', '/')))
                .Where(file => File.Exists(file.FullPath) && !filter.IsIgnored(file.RelativePath))
                .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToList();
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
            if (!Directory.Exists(gameDirectory))
                return null;

            LocatedManifest? latest = null;

            foreach (var directory in Directory.EnumerateDirectories(gameDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = Path.Combine(directory, BackupManifest.FileName);

                if (!File.Exists(path))
                    continue;

                try
                {
                    await using var stream = File.OpenRead(path);
                    var manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(stream, ManifestJsonOptions, cancellationToken);

                    if (manifest is null || !manifest.IsComplete || manifest.Units is null)
                        continue;

                    var profileMatches = manifest.ProfileId == profile.Id
                        || string.Equals(manifest.GameName, profile.GameName, StringComparison.OrdinalIgnoreCase);

                    if (profileMatches && (latest is null || manifest.CreatedAt > latest.Manifest.CreatedAt))
                        latest = new LocatedManifest(directory, manifest);
                }
                catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
                {
                }
            }

            return latest;
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
            var customRoot = unit.Component == BackupComponent.CustomFolders
                ? Path.GetFullPath(unit.OriginalRootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                : string.Empty;

            return $"{unit.Component}|{unit.RelativeDestination}|{customRoot}";
        }

        private static List<FileFingerprint> CollectFileMetadata(IReadOnlyList<ArchiveFileItem> files)
        {
            return files.Select(file =>
            {
                var info = new FileInfo(file.SourcePath);
                info.Refresh();

                return new FileFingerprint(
                    file.EntryName.Replace('\\', '/'),
                    info.Exists ? info.Length : -1,
                    info.Exists ? info.CreationTimeUtc.Ticks : -1,
                    info.Exists ? info.LastWriteTimeUtc.Ticks : -1,
                    info.Exists ? info.Attributes : 0,
                    string.Empty);
            }).OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static List<DirectoryFingerprint> CollectDirectoryMetadata(BackupManifestUnit unit)
        {
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

        private static string GetComponentRootDirectory(string backupRoot, string gameName, BackupComponent component)
        {
            var gameDirectory = Path.Combine(backupRoot, SanitizeFileName(gameName));
            var componentDirectory = Path.Combine(gameDirectory, GetComponentFolderName(component));
            Directory.CreateDirectory(componentDirectory);

            return componentDirectory;
        }

        private static string GetComponentFolderName(BackupComponent component)
        {
            return component switch
            {
                BackupComponent.StagingFolders => "staging",
                BackupComponent.GameConfigs => "configs",
                BackupComponent.VortexAppData => "appdata",
                BackupComponent.CustomFolders => "custom",
                _ => "misc"
            };
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

        private static string SanitizeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sanitized = new string(name.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim().TrimEnd('.');

            return sanitized.Length == 0 ? "unnamed" : sanitized;
        }

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
                return new FileInfo(path).Length;
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

            public void Report(ArchiveProgress value)
            {
                _bytes += value.BytesDelta;
                _current = value.CurrentFile;

                if (value.FileCompleted)
                    _files++;

                // Large files report per buffer, so throttle to keep the UI responsive.
                if (value.FileCompleted || _stopwatch.ElapsedMilliseconds - _lastReportMilliseconds >= 100)
                    Report();
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