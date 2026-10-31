using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

using UniversalBackupTool.Interface.Interfaces.Backups;
using UniversalBackupTool.Interface.Interfaces.Jobs;
using UniversalBackupTool.Model.Models.Backups;
using UniversalBackupTool.Model.Models.Jobs;
using UniversalBackupTool.Model.Models.Profiles;
using UniversalBackupTool.Model.Models.Settings;
using UniversalBackupTool.Service.Services.Backups;
using UniversalBackupTool.Service.Services.Settings;
using UniversalBackupTool.Service.Services.Snapshots;
using UniversalBackupTool.Services.Backups;

namespace UniversalBackupTool.Tests
{
    [TestFixture]
    public class IgnorePatternMatcherTests
    {
        private static IIgnoreFilter Create(params string[] patterns) => new IgnorePatternMatcher().Create(patterns);

        [Test]
        public void Wildcard_MatchesExtensionAtAnyDepth()
        {
            var filter = Create("*.log");

            Assert.Multiple(() =>
            {
                Assert.That(filter.IsIgnored("a.log"), Is.True);
                Assert.That(filter.IsIgnored("x/y/a.log"), Is.True);
                Assert.That(filter.IsIgnored("a.txt"), Is.False);
            });
        }

        [Test]
        public void FolderPattern_IgnoresEverythingInside()
        {
            var filter = Create("cache/");

            Assert.Multiple(() =>
            {
                Assert.That(filter.IsIgnored("cache/a.bin"), Is.True);
                Assert.That(filter.IsIgnored("mod/cache/a.bin"), Is.True);
                Assert.That(filter.IsIgnored("mycache/a.bin"), Is.False);
            });
        }

        [Test]
        public void PatternWithSlash_IsAnchoredToRoot()
        {
            var filter = Create("logs/*.txt");

            Assert.Multiple(() =>
            {
                Assert.That(filter.IsIgnored("logs/a.txt"), Is.True);
                Assert.That(filter.IsIgnored("other/logs/a.txt"), Is.False);
            });
        }

        [Test]
        public void CommentsAndBlankLines_AreIgnored()
        {
            var filter = Create("# comment", "", "   ");

            Assert.That(filter.IsIgnored("# comment"), Is.False);
        }

        [Test]
        public void Matching_IsCaseInsensitiveAndAcceptsBackslashes()
        {
            var filter = Create("*.LOG");

            Assert.Multiple(() =>
            {
                Assert.That(filter.IsIgnored("A.log"), Is.True);
                Assert.That(filter.IsIgnored(@"dir\a.log"), Is.True);
            });
        }
    }

    [TestFixture]
    public class ArchiveServiceTests
    {
        private string _dir = string.Empty;
        private readonly ArchiveService _service = new();
        private readonly AppSettings _settings = AppSettings.Default("x");

        [SetUp]
        public void SetUp() => _dir = TestDirectoryFactory.CreateTemporaryDirectory();

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }

        private string WriteFile(string name, string content)
        {
            var path = Path.Combine(_dir, name);
            File.WriteAllText(path, content);
            return path;
        }

        private static string ReadEntry(string archive, string entry)
        {
            using var zip = ZipFile.OpenRead(archive);
            using var reader = new StreamReader(zip.GetEntry(entry)!.Open());
            return reader.ReadToEnd();
        }

        [Test]
        public async Task Create_WritesAllFiles()
        {
            var a = WriteFile("a.txt", "A");
            var b = WriteFile("b.txt", "B");
            var archive = Path.Combine(_dir, "out", "x.zip");

            var result = await _service.CreateArchiveAsync(archive, [new(a, "a.txt"), new(b, "sub/b.txt")], _settings);

            Assert.Multiple(() =>
            {
                Assert.That(result.FilesWritten, Is.EqualTo(2));
                Assert.That(result.Failed, Is.Empty);
                Assert.That(ReadEntry(archive, "sub/b.txt"), Is.EqualTo("B"));
            });
        }

        [Test]
        public async Task Create_LockedFile_IsReportedAsFailedAndOthersAreWritten()
        {
            var a = WriteFile("a.txt", "A");
            var locked = WriteFile("locked.txt", "L");
            var archive = Path.Combine(_dir, "x.zip");

            using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var result = await _service.CreateArchiveAsync(archive, [new(a, "a.txt"), new(locked, "locked.txt")], _settings);

                Assert.Multiple(() =>
                {
                    Assert.That(result.FilesWritten, Is.EqualTo(1));
                    Assert.That(result.Failed.Select(f => f.EntryName), Is.EqualTo(new[] { "locked.txt" }));
                });
            }
        }

        [Test]
        public async Task Create_MissingFile_IsSkippedWithoutFailure()
        {
            var a = WriteFile("a.txt", "A");
            var archive = Path.Combine(_dir, "x.zip");

            var result = await _service.CreateArchiveAsync(archive, [new(a, "a.txt"), new(Path.Combine(_dir, "nope.txt"), "nope.txt")], _settings);

            Assert.Multiple(() =>
            {
                Assert.That(result.FilesWritten, Is.EqualTo(1));
                Assert.That(result.Failed, Is.Empty);
            });
        }

        [Test]
        public async Task Append_ReplacesEntryWithSameName()
        {
            var a = WriteFile("a.txt", "old");
            var archive = Path.Combine(_dir, "x.zip");
            await _service.CreateArchiveAsync(archive, [new(a, "a.txt")], _settings);

            File.WriteAllText(a, "new");
            var extra = WriteFile("c.txt", "C");
            await _service.AppendToArchiveAsync(archive, [new(a, "a.txt"), new(extra, "c.txt")], _settings);

            using var zip = ZipFile.OpenRead(archive);
            Assert.That(zip.Entries.Count, Is.EqualTo(2));
            zip.Dispose();
            Assert.That(ReadEntry(archive, "a.txt"), Is.EqualTo("new"));
        }

        [Test]
        public void Create_Cancelled_Throws()
        {
            var a = WriteFile("a.txt", "A");
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.That(
                async () => await _service.CreateArchiveAsync(Path.Combine(_dir, "x.zip"), [new(a, "a.txt")], _settings, null, cts.Token),
                Throws.InstanceOf<OperationCanceledException>());
        }
    }

    internal sealed class FakeJobStateService : IBackupJobStateService
    {
        public ActiveBackupJob? Saved { get; private set; }

        public int DeleteCount { get; private set; }

        public Task SaveAsync(ActiveBackupJob activeBackupJob, CancellationToken cancellationToken = default)
        {
            Saved = activeBackupJob;
            return Task.CompletedTask;
        }

        public Task<ActiveBackupJob?> LoadAsync(string filePath, CancellationToken cancellationToken = default) => Task.FromResult(Saved);

        public Task DeleteAsync(string filePath, CancellationToken cancellationToken = default)
        {
            Saved = null;
            DeleteCount++;
            return Task.CompletedTask;
        }
    }

    [TestFixture]
    public class BackupAndRestoreTests
    {
        private string _root = string.Empty;
        private string _staging = string.Empty;
        private string _drive = string.Empty;
        private FakeJobStateService _jobState = null!;
        private ArchiveService _archive = null!;
        private BackupJobRunner _runner = null!;
        private RestoreService _restore = null!;
        private AppSettings _settings = null!;

        [SetUp]
        public void SetUp()
        {
            _root = TestDirectoryFactory.CreateTemporaryDirectory();
            _staging = Path.Combine(_root, "staging");
            _drive = Path.Combine(_root, "drive");
            Directory.CreateDirectory(_staging);
            Directory.CreateDirectory(_drive);

            var fileSystem = new FakeFileSystemProvider();
            var paths = new FakeStoragePathsProvider(Path.Combine(_root, "app"));
            _jobState = new FakeJobStateService();
            _archive = new ArchiveService();
            _runner = new BackupJobRunner(
                _archive,
                fileSystem,
                new SnapshotService(fileSystem, new FakeSafetyBackupService()),
                new JsonSnapshotStore(),
                new IgnorePatternMatcher(),
                _jobState,
                paths);
            _restore = new RestoreService(new SafetyBackupService(paths, new JsonSettingsStore()));
            _settings = AppSettings.Default(_drive);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }

        private BackupProfile CreateProfile(bool useIgnore = true) => new()
        {
            GameName = "Test Game",
            VortexStagingPath = _staging,
            UseIgnoreList = useIgnore
        };

        private void Write(string relative, string content)
        {
            var path = Path.Combine(_staging, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        private static BackupOptions StagingOnly(bool monolithic = false) => new(true, false, false, false, monolithic);

        [Test]
        public async Task Backup_Staging_CreatesArchivePerModAndManifest()
        {
            Write("ModA/a.txt", "A");
            Write("ModB/sub/b.txt", "B");
            Write("root.txt", "R");

            var batch = await _runner.RunBackupAsync([CreateProfile()], StagingOnly(), _settings);
            var dir = batch.Results.Single().BackupDirectory;

            var manifest = await _restore.ReadManifestAsync(dir);
            var archiveRoot = Path.Combine(dir, "..");

            Assert.Multiple(() =>
            {
                Assert.That(batch.IsComplete, Is.True);
                Assert.That(batch.FilesArchived, Is.EqualTo(3));
                Assert.That(manifest!.Units, Has.Count.EqualTo(3));
                Assert.That(manifest.Units.SelectMany(unit => unit.FileFingerprints).All(file => file.Sha256.Length == 64), Is.True);
                Assert.That(manifest.Units.Single(unit => unit.RelativeDestination == "ModA").FileFingerprints.Single().Sha256, Is.EqualTo(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("A")))));
                Assert.That(manifest.Units.All(unit => File.Exists(Path.GetFullPath(Path.Combine(archiveRoot, unit.ArchiveFile)))), Is.True);
                Assert.That(manifest.Units.All(unit => unit.ArchiveRootRelativePath == ".."), Is.True);
                Assert.That(manifest.Units.Single(unit => unit.RelativeDestination == "ModA").ArchiveFile, Does.Match(@"^_archives/ModA__[A-F0-9]{12}\.zip$"));
                Assert.That(manifest.Units.Single(unit => unit.RelativeDestination == "ModB").ArchiveFile, Does.Match(@"^_archives/ModB__[A-F0-9]{12}\.zip$"));
                Assert.That(manifest.Units.Single(unit => unit.RelativeDestination == string.Empty).ArchiveFile, Does.Match(@"^_archives/_root_files__[A-F0-9]{12}\.zip$"));
                Assert.That(File.Exists(Path.Combine(dir, BackupManifest.FileName)), Is.True);
                Assert.That(Path.GetDirectoryName(dir), Does.StartWith(_drive));
            });
        }

        [Test]
        public async Task Backup_UnchangedSecondRun_ReusesModArchivesWithoutWritingFiles()
        {
            Write("ModA/a.txt", "A");
            Write("ModB/b.txt", "B");
            var profile = CreateProfile();

            var first = await _runner.RunBackupAsync([profile], StagingOnly(), _settings);
            var firstManifest = (await _restore.ReadManifestAsync(first.Results.Single().BackupDirectory))!;
            var second = await _runner.RunBackupAsync([profile], StagingOnly(), _settings);
            var secondManifest = (await _restore.ReadManifestAsync(second.Results.Single().BackupDirectory))!;

            Assert.Multiple(() =>
            {
                Assert.That(second.FilesArchived, Is.Zero);
                Assert.That(second.ModsArchived, Is.Zero);
                Assert.That(second.ModsReused, Is.EqualTo(2));
                Assert.That(secondManifest.Units.Select(unit => unit.ArchiveFile), Is.EquivalentTo(firstManifest.Units.Select(unit => unit.ArchiveFile)));
                Assert.That(Directory.GetFiles(Path.Combine(_drive, "Test Game", "staging", "_archives"), "*.zip"), Has.Length.EqualTo(2));
            });

            File.WriteAllText(Path.Combine(_staging, "ModA", "a.txt"), "changed");
            var plan = _restore.BuildPlan(secondManifest, [profile], [BackupComponent.StagingFolders]);
            await _restore.RestoreAsync(second.Results.Single().BackupDirectory, plan);
            Assert.That(File.ReadAllText(Path.Combine(_staging, "ModA", "a.txt")), Is.EqualTo("A"));
        }

        [Test]
        public async Task Backup_MetadataChangedButContentSame_ReusesArchiveAfterHashing()
        {
            Write("ModA/a.txt", "same bytes");
            var profile = CreateProfile();
            var first = await _runner.RunBackupAsync([profile], StagingOnly(), _settings);
            var firstManifest = (await _restore.ReadManifestAsync(first.Results.Single().BackupDirectory))!;
            var filePath = Path.Combine(_staging, "ModA", "a.txt");
            var originalCreationTime = File.GetCreationTimeUtc(filePath);

            File.SetCreationTimeUtc(filePath, originalCreationTime.AddMinutes(-2));
            var second = await _runner.RunBackupAsync([profile], StagingOnly(), _settings);
            var secondManifest = (await _restore.ReadManifestAsync(second.Results.Single().BackupDirectory))!;

            Assert.Multiple(() =>
            {
                Assert.That(second.FilesArchived, Is.Zero);
                Assert.That(second.ModsArchived, Is.Zero);
                Assert.That(second.ModsReused, Is.EqualTo(1));
                Assert.That(secondManifest.Units.Single().ArchiveFile, Is.EqualTo(firstManifest.Units.Single().ArchiveFile));
                Assert.That(secondManifest.Units.Single().FileFingerprints.Single().Sha256, Is.EqualTo(firstManifest.Units.Single().FileFingerprints.Single().Sha256));
            });
        }

        [Test]
        public async Task Backup_MultiComponent_UsesSeparateComponentFoldersAndRetentionPerComponent()
        {
            var appData = Path.Combine(_root, "appdata");
            Directory.CreateDirectory(appData);

            Write("ModA/a.txt", "A1");
            File.WriteAllText(Path.Combine(appData, "state.json"), "S1");

            var profile = CreateProfile() with { VortexAppDataPath = appData };
            var options = new BackupOptions(true, false, true, false);
            var settings = _settings with { MaxPreservedBackupsPerGame = 1 };

            await _runner.RunBackupAsync([profile], options, settings);

            Write("ModA/a.txt", "A2");
            File.WriteAllText(Path.Combine(appData, "state.json"), "S2");

            await _runner.RunBackupAsync([profile], options, settings);

            var gameRoot = Path.Combine(_drive, "Test Game");
            var stagingRoot = Path.Combine(gameRoot, "staging");
            var appDataRoot = Path.Combine(gameRoot, "appdata");
            var stagingSnapshots = Directory.GetDirectories(stagingRoot).Where(path => !string.Equals(Path.GetFileName(path), "_archives", StringComparison.OrdinalIgnoreCase)).ToList();
            var appDataSnapshots = Directory.GetDirectories(appDataRoot).Where(path => !string.Equals(Path.GetFileName(path), "_archives", StringComparison.OrdinalIgnoreCase)).ToList();

            Assert.Multiple(() =>
            {
                Assert.That(Directory.Exists(stagingRoot), Is.True);
                Assert.That(Directory.Exists(appDataRoot), Is.True);
                Assert.That(stagingSnapshots, Has.Count.EqualTo(1));
                Assert.That(appDataSnapshots, Has.Count.EqualTo(1));
            });
        }

        [Test]
        public async Task Retention_KeepsPausedSnapshotWithoutUsingASlot_AndResumeStillCompletesIt()
        {
            Write("ModA/locked.txt", "L");
            Write("ModB/b.txt", "B");
            var lockedPath = Path.Combine(_staging, "ModA", "locked.txt");
            var profile = CreateProfile();
            var settings = _settings with { MaxPreservedBackupsPerGame = 1 };

            BackupBatchResult paused;
            using (new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                paused = await _runner.RunBackupAsync([profile], StagingOnly(), settings);
            }

            var pausedDirectory = paused.Results.Single().BackupDirectory;
            var pausedJob = _jobState.Saved!;
            var pausedManifest = await ReadAnyManifestAsync(pausedDirectory);
            var pausedModB = pausedManifest.Units.Single(unit => unit.RelativeDestination == "ModB");
            var pausedModBArchive = Path.GetFullPath(Path.Combine(pausedDirectory, pausedModB.ArchiveRootRelativePath, pausedModB.ArchiveFile));

            var second = await _runner.RunBackupAsync([profile], StagingOnly(), settings);
            var secondDirectory = second.Results.Single().BackupDirectory;

            Assert.Multiple(() =>
            {
                Assert.That(Directory.Exists(pausedDirectory), Is.True, "paused snapshot must not take or lose a retention slot");
                Assert.That(Directory.Exists(secondDirectory), Is.True);
            });

            Write("ModB/b.txt", "B2");
            var third = await _runner.RunBackupAsync([profile], StagingOnly(), settings);

            Assert.Multiple(() =>
            {
                Assert.That(Directory.Exists(pausedDirectory), Is.True);
                Assert.That(Directory.Exists(secondDirectory), Is.False, "only the newest complete snapshot is kept");
                Assert.That(Directory.Exists(third.Results.Single().BackupDirectory), Is.True);
                Assert.That(File.Exists(pausedModBArchive), Is.True, "archives used by the paused snapshot must be kept");
            });

            var resumed = await _runner.ResumeBackupAsync(pausedJob, settings);

            Assert.Multiple(async () =>
            {
                Assert.That(resumed.IsComplete, Is.True);
                Assert.That(await _restore.ReadManifestAsync(pausedDirectory), Is.Not.Null);
            });
        }

        [Test]
        public async Task Retention_DeletesIncompleteSnapshotOfDiscardedJob()
        {
            Write("ModA/locked.txt", "L");
            var lockedPath = Path.Combine(_staging, "ModA", "locked.txt");
            var profile = CreateProfile();
            var settings = _settings with { MaxPreservedBackupsPerGame = 1 };

            BackupBatchResult paused;
            using (new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                paused = await _runner.RunBackupAsync([profile], StagingOnly(), settings);
            }

            await _jobState.DeleteAsync(string.Empty);

            var complete = await _runner.RunBackupAsync([profile], StagingOnly(), settings);

            Assert.Multiple(() =>
            {
                Assert.That(Directory.Exists(paused.Results.Single().BackupDirectory), Is.False);
                Assert.That(Directory.Exists(complete.Results.Single().BackupDirectory), Is.True);
            });
        }

        [Test]
        public async Task Retention_LeavesFoldersWithoutManifestUntouched()
        {
            Write("ModA/a.txt", "A");
            var profile = CreateProfile();
            var settings = _settings with { MaxPreservedBackupsPerGame = 1 };
            var stray = Path.Combine(_drive, "Test Game", "staging", "user folder");
            Directory.CreateDirectory(stray);
            File.WriteAllText(Path.Combine(stray, "notes.txt"), "keep me");

            await _runner.RunBackupAsync([profile], StagingOnly(), settings);
            Write("ModA/a.txt", "A2");
            await _runner.RunBackupAsync([profile], StagingOnly(), settings);

            Assert.That(File.Exists(Path.Combine(stray, "notes.txt")), Is.True);
        }

        private static async Task<BackupManifest> ReadAnyManifestAsync(string directory)
        {
            await using var stream = File.OpenRead(Path.Combine(directory, BackupManifest.FileName));
            var options = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
            return (await System.Text.Json.JsonSerializer.DeserializeAsync<BackupManifest>(stream, options))!;
        }

        [Test]
        public async Task Backup_ChangedOneMod_ArchivesOnlyThatMod()
        {
            Write("ModA/a.txt", "A");
            Write("ModB/b.txt", "B");
            var profile = CreateProfile();
            var first = await _runner.RunBackupAsync([profile], StagingOnly(), _settings);
            var firstManifest = (await _restore.ReadManifestAsync(first.Results.Single().BackupDirectory))!;

            Write("ModB/b.txt", "updated B");
            var second = await _runner.RunBackupAsync([profile], StagingOnly(), _settings);
            var secondManifest = (await _restore.ReadManifestAsync(second.Results.Single().BackupDirectory))!;
            var oldA = firstManifest.Units.Single(unit => unit.RelativeDestination == "ModA");
            var newA = secondManifest.Units.Single(unit => unit.RelativeDestination == "ModA");
            var oldB = firstManifest.Units.Single(unit => unit.RelativeDestination == "ModB");
            var newB = secondManifest.Units.Single(unit => unit.RelativeDestination == "ModB");

            Assert.Multiple(() =>
            {
                Assert.That(second.FilesArchived, Is.EqualTo(1));
                Assert.That(second.ModsArchived, Is.EqualTo(1));
                Assert.That(second.ModsReused, Is.EqualTo(1));
                Assert.That(newA.ArchiveFile, Is.EqualTo(oldA.ArchiveFile));
                Assert.That(newB.ArchiveFile, Is.Not.EqualTo(oldB.ArchiveFile));
            });
        }

        [Test]
        public async Task Backup_RemovedMod_IsOmittedFromNewSnapshotButOldSnapshotRemainsRestorable()
        {
            Write("ModA/a.txt", "A");
            Write("ModB/b.txt", "B");
            var profile = CreateProfile();
            var first = await _runner.RunBackupAsync([profile], StagingOnly(), _settings);
            var firstDirectory = first.Results.Single().BackupDirectory;
            File.Delete(Path.Combine(_staging, "ModB", "b.txt"));
            Directory.Delete(Path.Combine(_staging, "ModB"));

            var second = await _runner.RunBackupAsync([profile], StagingOnly(), _settings);
            var secondManifest = (await _restore.ReadManifestAsync(second.Results.Single().BackupDirectory))!;
            var firstManifest = await _restore.ReadManifestAsync(firstDirectory);

            Assert.Multiple(() =>
            {
                Assert.That(second.ModsRemoved, Is.EqualTo(1));
                Assert.That(secondManifest.Units.Select(unit => unit.RelativeDestination), Is.EqualTo(new[] { "ModA" }));
                Assert.That(firstManifest!.Units, Has.Count.EqualTo(2));
            });
        }

        [Test]
        public async Task Backup_Monolithic_CreatesSingleArchive()
        {
            Write("ModA/a.txt", "A");
            Write("ModB/b.txt", "B");

            var batch = await _runner.RunBackupAsync([CreateProfile()], StagingOnly(monolithic: true), _settings);
            var dir = batch.Results.Single().BackupDirectory;
            var manifest = await _restore.ReadManifestAsync(dir);

            Assert.Multiple(() =>
            {
                Assert.That(Directory.GetFiles(dir, "*.zip", SearchOption.AllDirectories), Has.Length.EqualTo(1));
                Assert.That(manifest!.IsMonolithic, Is.True);
            });

            var profile = CreateProfile();
            File.WriteAllText(Path.Combine(_staging, "ModA", "a.txt"), "changed");
            var plan = _restore.BuildPlan(manifest!, [profile], [BackupComponent.StagingFolders]);
            await _restore.RestoreAsync(dir, plan);
            Assert.That(File.ReadAllText(Path.Combine(_staging, "ModA", "a.txt")), Is.EqualTo("A"));
        }

        [Test]
        public async Task Backup_Staging_IsNeverFilteredByTheIgnoreList()
        {
            // The ignore list only applies to Game configs; staging is always backed up completely.
            Write("ModA/a.txt", "A");
            Write("ModA/skip.log", "L");
            var settings = _settings with { GlobalIgnorePatterns = ["*.log"] };

            var batch = await _runner.RunBackupAsync([CreateProfile(true)], StagingOnly(), settings);

            Assert.Multiple(() =>
            {
                Assert.That(batch.FilesArchived, Is.EqualTo(2));
                Assert.That(batch.Results.Single().IgnoredFiles, Is.Zero);
            });
        }

        [Test]
        public async Task Backup_GameConfigsWithoutSnapshot_AddsWarning()
        {
            Write("ModA/a.txt", "A");
            var profile = CreateProfile() with { GameRootPath = Path.Combine(_root, "game") };
            Directory.CreateDirectory(profile.GameRootPath!);

            var batch = await _runner.RunBackupAsync([profile], new BackupOptions(true, true, false, false), _settings);

            Assert.That(batch.Results.Single().Warnings, Is.Not.Empty);
        }

        [Test]
        public void Backup_WithoutBackupPath_Throws()
        {
            Write("ModA/a.txt", "A");

            Assert.That(
                async () => await _runner.RunBackupAsync([CreateProfile()], StagingOnly(), _settings with { GoogleDriveBackupPath = "" }),
                Throws.InstanceOf<InvalidOperationException>());
        }

        [Test]
        public void Backup_NothingToBackUp_Throws()
        {
            Assert.That(
                async () => await _runner.RunBackupAsync([CreateProfile()], StagingOnly(), _settings),
                Throws.InstanceOf<InvalidOperationException>());
        }

        [Test]
        public async Task Backup_Cancelled_RemovesPartialBackupFolder()
        {
            Write("ModA/a.txt", "A");
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.That(
                async () => await _runner.RunBackupAsync([CreateProfile()], StagingOnly(), _settings, null, cts.Token),
                Throws.InstanceOf<OperationCanceledException>());

            await Task.Yield();
            Assert.That(Directory.EnumerateFiles(_drive, "*", SearchOption.AllDirectories), Is.Empty);
        }

        [Test]
        public async Task Backup_LockedFile_SavesJobState_AndResumeCompletesIt()
        {
            Write("ModA/a.txt", "A");
            Write("ModA/locked.txt", "L");
            var lockedPath = Path.Combine(_staging, "ModA", "locked.txt");

            BackupBatchResult batch;
            using (new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                batch = await _runner.RunBackupAsync([CreateProfile()], StagingOnly(), _settings);
            }

            Assert.Multiple(() =>
            {
                Assert.That(batch.IsComplete, Is.False);
                Assert.That(_jobState.Saved, Is.Not.Null);
                Assert.That(_jobState.Saved!.FailedFiles, Has.Count.EqualTo(1));
            });

            var pendingMove = _jobState.Saved!.PendingArchiveMoves.Single();
            File.Move(pendingMove.TemporaryPath, pendingMove.FinalPath);

            var snapshotDirectory = batch.Results.Single().BackupDirectory;
            Assert.That(await _restore.ReadManifestAsync(snapshotDirectory), Is.Null);

            var resumed = await _runner.ResumeBackupAsync(_jobState.Saved!, _settings);
            var manifest = await _restore.ReadManifestAsync(snapshotDirectory);
            var unit = manifest!.Units.Single();
            var archiveRoot = Path.GetFullPath(Path.Combine(snapshotDirectory, unit.ArchiveRootRelativePath));

            Assert.Multiple(() =>
            {
                Assert.That(resumed.IsComplete, Is.True);
                Assert.That(_jobState.Saved, Is.Null);
                Assert.That(_jobState.DeleteCount, Is.EqualTo(1));
                Assert.That(manifest.IsComplete, Is.True);
                Assert.That(manifest.Units.SelectMany(item => item.FileFingerprints).All(file => file.Sha256.Length == 64), Is.True);
            });

            using var zip = ZipFile.OpenRead(Path.Combine(archiveRoot, unit.ArchiveFile));
            Assert.That(zip.Entries.Select(e => e.FullName), Is.EquivalentTo(new[] { "a.txt", "locked.txt" }));
        }

        [Test]
        public async Task Restore_RoundTrip_RestoresFilesAndCreatesSafetyBackup()
        {
            Write("ModA/a.txt", "original");
            Write("ModB/b.txt", "B");
            var profile = CreateProfile();
            var batch = await _runner.RunBackupAsync([profile], StagingOnly(), _settings);
            var dir = batch.Results.Single().BackupDirectory;

            File.WriteAllText(Path.Combine(_staging, "ModA", "a.txt"), "changed");
            File.Delete(Path.Combine(_staging, "ModB", "b.txt"));

            var manifest = await _restore.ReadManifestAsync(dir);
            var plan = _restore.BuildPlan(manifest!, [profile], [BackupComponent.StagingFolders]);
            var result = await _restore.RestoreAsync(dir, plan);

            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllText(Path.Combine(_staging, "ModA", "a.txt")), Is.EqualTo("original"));
                Assert.That(File.ReadAllText(Path.Combine(_staging, "ModB", "b.txt")), Is.EqualTo("B"));
                Assert.That(result.FilesRestored, Is.EqualTo(2));
                Assert.That(result.SafetyBackupPath, Is.Not.Null);
                Assert.That(File.Exists(result.SafetyBackupPath), Is.True);
            });

            using var safety = ZipFile.OpenRead(result.SafetyBackupPath!);
            Assert.That(safety.Entries, Has.Count.EqualTo(1));
        }

        [Test]
        public async Task BuildPlan_ProfileMatchedByNameOrFallsBackToOriginalPath()
        {
            Write("ModA/a.txt", "A");
            var profile = CreateProfile();
            var batch = await _runner.RunBackupAsync([profile], StagingOnly(), _settings);
            var manifest = (await _restore.ReadManifestAsync(batch.Results.Single().BackupDirectory))!;

            var movedStaging = Path.Combine(_root, "moved");
            var renamed = profile with { Id = Guid.NewGuid(), VortexStagingPath = movedStaging };

            var byName = _restore.BuildPlan(manifest, [renamed], [BackupComponent.StagingFolders]);
            var fallback = _restore.BuildPlan(manifest, [], [BackupComponent.StagingFolders]);

            Assert.Multiple(() =>
            {
                Assert.That(byName.Single().RootPath, Is.EqualTo(movedStaging));
                Assert.That(byName.Single().UsesOriginalPath, Is.False);
                Assert.That(fallback.Single().RootPath, Is.EqualTo(_staging));
                Assert.That(fallback.Single().UsesOriginalPath, Is.True);
            });
        }

        [Test]
        public async Task ReadManifest_MissingOrInvalid_ReturnsNull()
        {
            var empty = Path.Combine(_root, "empty");
            Directory.CreateDirectory(empty);
            Assert.That(await _restore.ReadManifestAsync(empty), Is.Null);

            await File.WriteAllTextAsync(Path.Combine(empty, BackupManifest.FileName), "{ not json");
            Assert.That(await _restore.ReadManifestAsync(empty), Is.Null);
        }

        [Test]
        public async Task Restore_ZipSlipEntry_IsRejected()
        {
            var backupDir = Path.Combine(_root, "evil");
            Directory.CreateDirectory(backupDir);
            using (var zip = ZipFile.Open(Path.Combine(backupDir, "evil.zip"), ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("../../escaped.txt");
                await using var stream = entry.Open();
                await stream.WriteAsync("x"u8.ToArray());
            }

            var destination = Path.Combine(_root, "dest", "inner");
            var unit = new BackupManifestUnit(BackupComponent.StagingFolders, "evil.zip", string.Empty, destination, string.Empty);
            var plan = new[] { new RestorePlanItem(unit, destination, destination, true) };

            Assert.That(async () => await _restore.RestoreAsync(backupDir, plan), Throws.Exception);
            Assert.That(File.Exists(Path.Combine(_root, "escaped.txt")), Is.False);
        }

        [Test]
        public async Task Restore_MissingArchive_Throws()
        {
            var backupDir = Path.Combine(_root, "none");
            Directory.CreateDirectory(backupDir);
            var destination = Path.Combine(_root, "dest");
            var unit = new BackupManifestUnit(BackupComponent.StagingFolders, "missing.zip", string.Empty, destination, string.Empty);

            Assert.That(
                async () => await _restore.RestoreAsync(backupDir, [new RestorePlanItem(unit, destination, destination, true)]),
                Throws.Exception);
            await Task.CompletedTask;
        }

        [Test]
        public void Restore_ArchiveRootTraversal_IsRejected()
        {
            var backupDir = Path.Combine(_root, "snapshot");
            Directory.CreateDirectory(backupDir);
            var destination = Path.Combine(_root, "dest");
            var unit = new BackupManifestUnit(BackupComponent.StagingFolders, "outside.zip", string.Empty, destination, string.Empty)
            {
                ArchiveRootRelativePath = "..\\.."
            };

            Assert.That(
                async () => await _restore.RestoreAsync(backupDir, [new RestorePlanItem(unit, destination, destination, true)]),
                Throws.InstanceOf<InvalidDataException>());
        }
    }
}