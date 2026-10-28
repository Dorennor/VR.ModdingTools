using System.Diagnostics;
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
                var prefix = profiles.Count > 1 ? $"{profile.GameName} ({index + 1}/{profiles.Count})" : profile.GameName;

                var result = await BackupProfileAsync(profile, options, settings, prefix, progress, cancellationToken);

                results.Add(result);
                failedFiles.AddRange(result.FailedFiles);
            }

            if (failedFiles.Count > 0)
            {
                var firstFailed = results.First(result => result.FailedFiles.Count > 0);
                var profile = profiles.First(candidate => candidate.GameName == firstFailed.GameName);

                var job = ActiveBackupJob.Create(profile.Id, firstFailed.BackupDirectory, profile.GameRootPath ?? profile.VortexStagingPath ?? string.Empty) with
                {
                    FailedFiles = failedFiles,
                    FailedPaths = failedFiles.Select(file => file.SourcePath).ToList()
                };

                await _backupJobStateService.SaveAsync(job, cancellationToken);
            }

            return new BackupBatchResult(results, failedFiles, results.Sum(result => result.FilesArchived));
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
                .ToList();

            foreach (var group in groups)
            {
                if (!File.Exists(group.Key))
                    throw new InvalidOperationException($"The archive of the paused job no longer exists: {group.Key}. Discard the paused job.");
            }

            var totalFiles = job.FailedFiles.Count;
            var totalBytes = job.FailedFiles.Sum(file => GetLength(file.SourcePath));
            var tracker = new ProgressTracker("Retrying locked files", totalBytes, totalFiles, progress);

            var remaining = new List<FailedBackupFile>();
            var archived = 0;

            foreach (var group in groups)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var items = group.Select(file => new ArchiveFileItem(file.SourcePath, file.EntryName)).ToList();
                var result = await _archiveService.AppendToArchiveAsync(group.Key, items, settings, tracker, cancellationToken);

                archived += result.FilesWritten;
                remaining.AddRange(result.Failed.Select(item => new FailedBackupFile(group.Key, item.EntryName, item.SourcePath)));
            }

            tracker.Finish();

            var paths = _storagePathsProvider.GetPaths();

            if (remaining.Count == 0)
            {
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

        private async Task<BackupResult> BackupProfileAsync(
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

            var backupDirectory = CreateBackupDirectory(settings.GoogleDriveBackupPath, profile.GameName);

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
                    Units = units.Select(unit => unit.Manifest).ToList()
                };

                await WriteManifestAsync(backupDirectory, manifest, cancellationToken);

                return new BackupResult(profile.GameName, backupDirectory, archived, warnings, failedFiles);
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

        private static string CreateBackupDirectory(string backupRoot, string gameName)
        {
            var gameDirectory = Path.Combine(backupRoot, SanitizeFileName(gameName));
            var baseName = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            var candidate = Path.Combine(gameDirectory, baseName);
            var suffix = 1;

            while (Directory.Exists(candidate))
            {
                suffix++;
                candidate = Path.Combine(gameDirectory, $"{baseName}_{suffix}");
            }

            Directory.CreateDirectory(candidate);

            return candidate;
        }

        private static async Task WriteManifestAsync(string backupDirectory, BackupManifest manifest, CancellationToken cancellationToken)
        {
            var manifestPath = Path.Combine(backupDirectory, BackupManifest.FileName);

            await using var stream = File.Create(manifestPath);
            await JsonSerializer.SerializeAsync(stream, manifest, ManifestJsonOptions, cancellationToken);
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

        private sealed record SourceFile(string FullPath, string RelativePath);

        private sealed record UnitPlan(BackupManifestUnit Manifest, List<ArchiveFileItem> Files);

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