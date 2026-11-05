using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;

using UniversalBackupTool.Model.Models.Backups;
using UniversalBackupTool.Model.Models.Profiles;
using UniversalBackupTool.Model.Models.Settings;
using UniversalBackupTool.Service.Services.Backups;
using UniversalBackupTool.Service.Services.Profiles;
using UniversalBackupTool.Service.Services.Snapshots;
using UniversalBackupTool.Services;

namespace UniversalBackupTool.Tests
{
    /// <summary>
    /// The Full game component: one working zip of the game folder in the profile's export folder, with the old archive
    /// deleted (never overwritten) only after everything that can stop the run has been checked.
    /// </summary>
    [TestFixture]
    public class FullGameBackupTests
    {
        private static readonly BackupOptions FullGameOnly = new(false, false, false, false, IncludeFullGame: true);

        private string _root = string.Empty;
        private string _game = string.Empty;
        private string _export = string.Empty;
        private FakeJobStateService _jobState = null!;
        private BackupJobRunner _runner = null!;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateHardLinkW(string newFileName, string existingFileName, IntPtr securityAttributes);

        [SetUp]
        public void SetUp()
        {
            _root = TestDirectoryFactory.CreateTemporaryDirectory();
            _game = Path.Combine(_root, "game");
            _export = Path.Combine(_root, "export");
            _jobState = new FakeJobStateService();
            _runner = CreateRunner(new ArchiveService());

            Write(_game, "game.exe", "vanilla");
            Write(_game, "Data/plugin.esp", "mod");
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }

        private BackupJobRunner CreateRunner(UniversalBackupTool.Interface.Interfaces.Backups.IArchiveService archiveService)
        {
            var fileSystem = new FileSystemProvider();

            return new BackupJobRunner(
                archiveService,
                fileSystem,
                new SnapshotService(fileSystem, new FakeSafetyBackupService()),
                new JsonSnapshotStore(),
                new IgnorePatternMatcher(),
                _jobState,
                new FakeStoragePathsProvider(Path.Combine(_root, "app")));
        }

        private static string Write(string root, string relative, string content)
        {
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }

        private BackupProfile CreateProfile(string name = "Witcher", string? game = null, bool useIgnoreList = true) => new()
        {
            GameName = name,
            UsesVortex = false,
            GameRootPath = game ?? _game,
            UseIgnoreList = useIgnoreList,
            UseFullGameExportPath = true,
            FullGameExportPath = _export
        };

        // No backup destination: a full game backup only needs the export folder.
        private static AppSettings Settings(params string[] ignorePatterns) =>
            AppSettings.Default(string.Empty) with { GlobalIgnorePatterns = [.. ignorePatterns] };

        private string ArchivePath(string name = "Witcher") => Path.Combine(_export, name + ".zip");

        private static Dictionary<string, string> ReadArchive(string path)
        {
            using var archive = ZipFile.OpenRead(path);

            return archive.Entries.ToDictionary(entry => entry.FullName, entry =>
            {
                using var reader = new StreamReader(entry.Open());
                return reader.ReadToEnd();
            });
        }

        [Test]
        public async Task Archive_HoldsTheWholeGameFolderWithRelativePaths()
        {
            var batch = await _runner.RunBackupAsync([CreateProfile()], FullGameOnly, Settings());

            Assert.That(batch.IsComplete, Is.True);
            Assert.That(batch.Results.Single().FullGameArchivePath, Is.EqualTo(ArchivePath()));
            Assert.That(batch.FilesArchived, Is.EqualTo(2));
            Assert.That(ReadArchive(ArchivePath()), Is.EquivalentTo(new Dictionary<string, string>
            {
                ["game.exe"] = "vanilla",
                ["Data/plugin.esp"] = "mod"
            }));
            Assert.That(Directory.GetFiles(_export), Has.Length.EqualTo(1), "no temporary file stays behind");
        }

        [Test]
        public async Task Links_AreStoredAsRealFiles()
        {
            var staging = Path.Combine(_root, "staging");
            var hardlinkTarget = Write(staging, "hard.esp", "hardlinked mod");
            var junctionTarget = Path.Combine(staging, "JunctionMod");

            Write(junctionTarget, "textures/a.dds", "junction mod");
            Assert.That(CreateHardLinkW(Path.Combine(_game, "Data", "hard.esp"), hardlinkTarget, IntPtr.Zero), Is.True);

            using (var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{Path.Combine(_game, "Data", "JunctionMod")}\" \"{junctionTarget}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            })!)
            {
                process.WaitForExit();
                Assert.That(process.ExitCode, Is.Zero);
            }

            try
            {
                await _runner.RunBackupAsync([CreateProfile()], FullGameOnly, Settings());

                var entries = ReadArchive(ArchivePath());

                Assert.That(entries["Data/hard.esp"], Is.EqualTo("hardlinked mod"));
                Assert.That(entries["Data/JunctionMod/textures/a.dds"], Is.EqualTo("junction mod"));

                // The sources are read, never changed.
                Assert.That(File.ReadAllText(hardlinkTarget), Is.EqualTo("hardlinked mod"));
            }
            finally
            {
                // A recursive delete of the test folder cannot go through a junction.
                Directory.Delete(Path.Combine(_game, "Data", "JunctionMod"));
            }
        }

        [Test]
        public async Task FileSymlink_IsStoredWithItsTargetContent_BrokenOneIsReported()
        {
            var target = Write(_root, "elsewhere/mod.esp", "symlinked mod");

            try
            {
                File.CreateSymbolicLink(Path.Combine(_game, "Data", "link.esp"), target);
                File.CreateSymbolicLink(Path.Combine(_game, "Data", "broken.esp"), Path.Combine(_root, "missing.esp"));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Assert.Ignore("This machine does not allow creating symbolic links.");
            }

            var batch = await _runner.RunBackupAsync([CreateProfile()], FullGameOnly, Settings());
            var entries = ReadArchive(ArchivePath());

            Assert.That(entries["Data/link.esp"], Is.EqualTo("symlinked mod"));
            Assert.That(entries, Does.Not.ContainKey("Data/broken.esp"));
            Assert.That(batch.Results.Single().Warnings, Has.Some.Contains("broken.esp"));
        }

        [Test]
        public async Task EmptyFolders_AreKept()
        {
            Directory.CreateDirectory(Path.Combine(_game, "Saves", "Slot1"));

            await _runner.RunBackupAsync([CreateProfile()], FullGameOnly, Settings());

            var extracted = Path.Combine(_root, "extracted");

            ZipFile.ExtractToDirectory(ArchivePath(), extracted);

            Assert.That(Directory.Exists(Path.Combine(extracted, "Saves", "Slot1")), Is.True);
        }

        [Test]
        public async Task IgnoreList_IsAppliedAndReported_AndTheProfileSwitchTurnsItOff()
        {
            Write(_game, "vortex.deployment.json", "{ }");

            var filtered = await _runner.RunBackupAsync([CreateProfile()], FullGameOnly, Settings("vortex.deployment*.json"));

            Assert.That(ReadArchive(ArchivePath()), Does.Not.ContainKey("vortex.deployment.json"));
            Assert.That(filtered.Results.Single().IgnoredFiles, Is.EqualTo(1));

            await _runner.RunBackupAsync([CreateProfile(useIgnoreList: false)], FullGameOnly, Settings("vortex.deployment*.json"));

            Assert.That(ReadArchive(ArchivePath()), Does.ContainKey("vortex.deployment.json"));
        }

        [Test]
        public async Task CompressionSettings_AreApplied()
        {
            var content = new string('a', 100_000);

            Write(_game, "compressible.txt", content);
            Write(_game, "stored.ba2", content);

            var settings = Settings() with { NoCompressionExtensions = [".ba2"] };

            await _runner.RunBackupAsync([CreateProfile()], FullGameOnly, settings);

            using (var archive = ZipFile.OpenRead(ArchivePath()))
            {
                Assert.That(archive.GetEntry("compressible.txt")!.CompressedLength, Is.LessThan(content.Length));
                Assert.That(archive.GetEntry("stored.ba2")!.CompressedLength, Is.EqualTo(content.Length));
            }

            var batch = await _runner.RunBackupAsync([CreateProfile()], FullGameOnly, settings with { StoreComponentsLargerThanBytes = 1000 });

            using (var archive = ZipFile.OpenRead(ArchivePath()))
                Assert.That(archive.GetEntry("compressible.txt")!.CompressedLength, Is.EqualTo(content.Length));

            Assert.That(batch.Results.Single().Warnings, Has.Some.Contains("without compression"));
        }

        [Test]
        public async Task OldArchive_IsDeletedBeforeTheNewOneIsStarted_NeverOverwritten()
        {
            var old = Write(_export, "Witcher.zip", "old archive");
            var leftover = Write(_export, "Witcher.zip.abc.pending", "unfinished");
            var other = Write(_export, "Witcher 2.zip", "another game");
            var seenWhenWriting = new List<string>();
            var runner = CreateRunner(new SpyArchiveService(new ArchiveService(), () => seenWhenWriting.AddRange(Directory.GetFiles(_export).Select(Path.GetFileName)!)));

            await runner.RunBackupAsync([CreateProfile()], FullGameOnly, Settings());

            Assert.That(seenWhenWriting, Is.EqualTo(new[] { "Witcher 2.zip" }), "only other games' files may exist when writing starts");
            Assert.That(ReadArchive(old), Does.ContainKey("game.exe"));
            Assert.That(File.Exists(leftover), Is.False);
            Assert.That(File.ReadAllText(other), Is.EqualTo("another game"));
        }

        [Test]
        public async Task LockedFile_StopsThatGameBeforeTheOldArchiveIsTouched_AndOtherGamesStillRun()
        {
            var old = Write(_export, "Witcher.zip", "old archive");
            var locked = Write(_game, "locked.dat", "in use");
            var otherGame = Path.Combine(_root, "other");

            Write(otherGame, "other.exe", "other");

            BackupBatchResult batch;

            using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                batch = await _runner.RunBackupAsync([CreateProfile(), CreateProfile("Other", otherGame)], FullGameOnly, Settings());

            Assert.That(File.ReadAllText(old), Is.EqualTo("old archive"));
            Assert.That(batch.Warnings, Has.Some.Contains("old archive was not touched").And.Contains("locked.dat"));
            Assert.That(batch.Results.Single().GameName, Is.EqualTo("Other"));
            Assert.That(ReadArchive(ArchivePath("Other")), Does.ContainKey("other.exe"));
        }

        [Test]
        public async Task NothingUsable_LeavesTheOldArchiveAlone()
        {
            var old = Write(_export, "Witcher.zip", "old archive");
            var emptyGame = Path.Combine(_root, "empty");

            Directory.CreateDirectory(emptyGame);

            foreach (var game in new[] { emptyGame, Path.Combine(_root, "missing") })
            {
                var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => _runner.RunBackupAsync([CreateProfile(game: game)], FullGameOnly, Settings()));

                Assert.That(exception!.Message, Does.Contain("no full game backup"));
                Assert.That(File.ReadAllText(old), Is.EqualTo("old archive"));
            }
        }

        [Test]
        public async Task ExportFolderInsideTheGameFolder_IsRefused()
        {
            var profile = CreateProfile() with { FullGameExportPath = Path.Combine(_game, "Backups") };

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => _runner.RunBackupAsync([profile], FullGameOnly, Settings()));

            Assert.That(exception!.Message, Does.Contain("inside the game folder"));
            Assert.That(Directory.Exists(Path.Combine(_game, "Backups")), Is.False);
        }

        [Test]
        public async Task GameWithoutExportFolder_IsSkippedWithAWarning_AndTheCheckboxDecides()
        {
            var otherGame = Path.Combine(_root, "other");

            Write(otherGame, "other.exe", "other");

            var unchecked_ = CreateProfile("Unchecked") with { UseFullGameExportPath = false };

            var batch = await _runner.RunBackupAsync([unchecked_, CreateProfile("Other", otherGame)], FullGameOnly, Settings());

            Assert.That(batch.Warnings, Has.Some.Contains("Unchecked").And.Contains("no export folder"));
            Assert.That(File.Exists(ArchivePath("Unchecked")), Is.False);
            Assert.That(File.Exists(ArchivePath("Other")), Is.True);
        }

        [Test]
        public async Task NoGameHasAnExportFolder_ReportsWhy()
        {
            var profile = CreateProfile() with { UseFullGameExportPath = false };

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => _runner.RunBackupAsync([profile], FullGameOnly, Settings()));

            Assert.That(exception!.Message, Does.Contain("no export folder"));
        }

        [Test]
        public async Task Cancelling_RemovesTheUnfinishedArchive()
        {
            using var cancellation = new CancellationTokenSource();
            var runner = CreateRunner(new SpyArchiveService(new ArchiveService(), cancellation.Cancel));

            await Assert.ThrowsAsync(
                Is.InstanceOf<OperationCanceledException>(),
                () => runner.RunBackupAsync([CreateProfile()], FullGameOnly, Settings(), null, cancellation.Token));

            Assert.That(Directory.GetFiles(_export), Is.Empty);
        }

        [Test]
        public async Task FileLockedWhileArchiving_IsRetriedInTheSameRun()
        {
            var locked = Write(_game, "locked.dat", "in use");
            var last = Write(_game, "last.dat", "last");

            // Locked for the first pass, free again once the next file is done.
            await using var stream = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            var written = await new ArchiveService().CreateArchiveAsync(
                Path.Combine(_root, "retry.zip"),
                [new ArchiveFileItem(locked, "locked.dat"), new ArchiveFileItem(last, "last.dat")],
                Settings(),
                new ReleaseOnProgress(stream));

            Assert.That(written.Failed, Is.Empty);
            Assert.That(written.FilesWritten, Is.EqualTo(2));
            Assert.That(ReadArchive(Path.Combine(_root, "retry.zip"))["locked.dat"], Is.EqualTo("in use"));
        }

        [Test]
        public async Task FileStillLockedAfterTheRetry_WaitsForContinueBackup_ThenTheArchiveIsPublished()
        {
            var locked = Write(_game, "locked.dat", "in use");
            FileStream? stream = null;

            // The lock starts after the check that runs before the old archive is deleted.
            var runner = CreateRunner(new SpyArchiveService(new ArchiveService(), () =>
                stream = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None)));

            var batch = await runner.RunBackupAsync([CreateProfile()], FullGameOnly, Settings());

            stream!.Dispose();

            Assert.That(batch.FailedFiles.Single().SourcePath, Is.EqualTo(locked));
            Assert.That(File.Exists(ArchivePath()), Is.False, "the archive is not published while a file is missing");

            var job = _jobState.Saved!;
            var resumed = await _runner.ResumeBackupAsync(job, Settings());

            Assert.That(resumed.IsComplete, Is.True);
            Assert.That(ReadArchive(ArchivePath())["locked.dat"], Is.EqualTo("in use"));
            Assert.That(Directory.GetFiles(_export), Has.Length.EqualTo(1));
        }

        [Test]
        public void Find_ListsOnlyExistingArchivesOfGamesWithAnExportFolder()
        {
            Write(_export, "Witcher.zip", "12345");
            Write(_export, "Off.zip", "12345");

            var found = _runner.FindExistingFullGameArchives(
            [
                CreateProfile(),
                CreateProfile("No archive yet"),
                CreateProfile("Off") with { UseFullGameExportPath = false }
            ]);

            Assert.That(found.Single(), Is.EqualTo(new ExistingFullGameArchive("Witcher", ArchivePath(), 5)));
        }

        [Test]
        public async Task ProfilesSavedBeforeTheOption_LoadWithItOff_AndTheFolderSurvivesASave()
        {
            var path = Path.Combine(_root, "profiles.json");
            var store = new JsonProfileStore();

            await File.WriteAllTextAsync(path, """[{ "GameName": "Old", "GameRootPath": "D:\\Games\\Old" }]""");

            var loaded = (await store.LoadAsync(path)).Single();

            Assert.That(loaded.UseFullGameExportPath, Is.False);
            Assert.That(loaded.GetFullGameExportFolder(), Is.Null);

            await store.SaveAsync(path, [loaded with { FullGameExportPath = @"G:\Drive", UseFullGameExportPath = true }]);

            Assert.That((await store.LoadAsync(path)).Single().GetFullGameExportFolder(), Is.EqualTo(@"G:\Drive"));
        }

        /// <summary>
        /// Runs a callback at the moment the archive is about to be written.
        /// </summary>
        private sealed class SpyArchiveService(ArchiveService inner, Action beforeWrite) : UniversalBackupTool.Interface.Interfaces.Backups.IArchiveService
        {
            public Task<ArchiveWriteResult> CreateArchiveAsync(string archivePath, IReadOnlyList<ArchiveFileItem> files, AppSettings settings, IProgress<ArchiveProgress>? progress = null, CancellationToken cancellationToken = default)
            {
                beforeWrite();
                return inner.CreateArchiveAsync(archivePath, files, settings, progress, cancellationToken);
            }

            public Task<ArchiveWriteResult> AppendToArchiveAsync(string archivePath, IReadOnlyList<ArchiveFileItem> files, AppSettings settings, IProgress<ArchiveProgress>? progress = null, CancellationToken cancellationToken = default)
                => inner.AppendToArchiveAsync(archivePath, files, settings, progress, cancellationToken);
        }

        /// <summary>
        /// Frees the locked file as soon as any other file has been archived.
        /// </summary>
        private sealed class ReleaseOnProgress(FileStream stream) : IProgress<ArchiveProgress>
        {
            public void Report(ArchiveProgress value)
            {
                if (value.FileCompleted)
                    stream.Dispose();
            }
        }
    }
}
