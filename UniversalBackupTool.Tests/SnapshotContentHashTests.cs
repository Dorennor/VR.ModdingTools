using UniversalBackupTool.Model.Models.Backups;
using UniversalBackupTool.Model.Models.Profiles;
using UniversalBackupTool.Model.Models.Settings;
using UniversalBackupTool.Model.Models.Snapshots;
using UniversalBackupTool.Service.Services.Backups;
using UniversalBackupTool.Service.Services.Snapshots;
using UniversalBackupTool.Services;

namespace UniversalBackupTool.Tests
{
    /// <summary>
    /// The baseline snapshot stores SHA-256 hashes, so a vanilla file rewritten with the same content (reinstall, file
    /// verification) is not a "changed game file"; its new timestamp is then saved into the snapshot.
    /// </summary>
    [TestFixture]
    public class SnapshotContentHashTests
    {
        private string _root = string.Empty;
        private string _game = string.Empty;
        private FakeStoragePathsProvider _paths = null!;
        private SnapshotService _service = null!;
        private JsonSnapshotStore _store = null!;

        [SetUp]
        public void SetUp()
        {
            _root = TestDirectoryFactory.CreateTemporaryDirectory();
            _game = Path.Combine(_root, "game");
            _paths = new FakeStoragePathsProvider(Path.Combine(_root, "app"));
            _service = new SnapshotService(new FileSystemProvider(), new FakeSafetyBackupService());
            _store = new JsonSnapshotStore();

            Write("game.exe", "vanilla exe");
            Write("Data/main.bsa", "vanilla archive");
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }

        private string Write(string relative, string content)
        {
            var path = Path.Combine(_game, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }

        /// <summary>
        /// Same bytes, new timestamp: what a reinstall of the same game version leaves behind.
        /// </summary>
        private void Rewrite(string relative, string content)
        {
            var path = Write(relative, content);
            File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddHours(1));
        }

        [Test]
        public async Task CreateSnapshot_HashesRegularFiles()
        {
            var snapshot = await _service.CreateSnapshotAsync(Guid.NewGuid(), _game);

            Assert.Multiple(() =>
            {
                Assert.That(snapshot.Files["game.exe"].Sha256, Has.Length.EqualTo(64));
                Assert.That(snapshot.Files["Data/main.bsa"].Sha256, Is.Not.EqualTo(snapshot.Files["game.exe"].Sha256));
            });
        }

        [Test]
        public async Task Compare_WithContentCheck_IgnoresRewrittenIdenticalFiles_AndReportsRealChanges()
        {
            var baseline = await _service.CreateSnapshotAsync(Guid.NewGuid(), _game);
            Rewrite("game.exe", "vanilla exe");
            Rewrite("Data/main.bsa", "patched archive"); // same size, different content
            Write("user.ini", "new");

            var comparison = await _service.CompareAsync(_game, baseline, verifyContent: true);

            Assert.Multiple(() =>
            {
                Assert.That(comparison.Changes.Keys, Is.EquivalentTo(new[] { "Data/main.bsa", "user.ini" }));
                Assert.That(comparison.Reanchored.Select(file => file.RelativePath), Is.EqualTo(new[] { "game.exe" }));
                Assert.That(comparison.Reanchored[0].LastWriteTimeUtc, Is.EqualTo(File.GetLastWriteTimeUtc(Path.Combine(_game, "game.exe"))));
                Assert.That(comparison.Reanchored[0].Sha256, Is.EqualTo(baseline.Files["game.exe"].Sha256));
                Assert.That(comparison.UnverifiedFiles, Is.Zero);
            });
        }

        [Test]
        public async Task Compare_WithoutContentCheck_JudgesByMetadataOnly()
        {
            var baseline = await _service.CreateSnapshotAsync(Guid.NewGuid(), _game);
            Rewrite("game.exe", "vanilla exe");

            var changes = await _service.GetChangesAsync(_game, baseline);

            Assert.That(changes.Keys, Is.EquivalentTo(new[] { "game.exe" }));
        }

        [Test]
        public async Task Compare_SnapshotWithoutHashes_ReportsFilesAsModifiedAndUnverified()
        {
            var hashed = await _service.CreateSnapshotAsync(Guid.NewGuid(), _game);
            var legacy = hashed with
            {
                Files = hashed.Files.ToDictionary(pair => pair.Key, pair => pair.Value with { Sha256 = null }, StringComparer.OrdinalIgnoreCase)
            };
            Rewrite("game.exe", "vanilla exe");

            var comparison = await _service.CompareAsync(_game, legacy, verifyContent: true);

            Assert.Multiple(() =>
            {
                Assert.That(comparison.Changes.Keys, Is.EquivalentTo(new[] { "game.exe" }));
                Assert.That(comparison.UnverifiedFiles, Is.EqualTo(1));
                Assert.That(comparison.Reanchored, Is.Empty);
            });
        }

        [Test]
        public async Task UpdateEntries_ChangesKnownEntries_KeepsCreationTime_AndAddsNothing()
        {
            var directory = _paths.GetPaths().SnapshotsDirectory;
            var snapshot = await _service.CreateSnapshotAsync(Guid.NewGuid(), _game);
            await _store.SaveAsync(directory, snapshot);
            var snapshotFile = Path.Combine(directory, snapshot.ProfileId.ToString("D") + ".json");
            var createdAt = new DateTime(2025, 5, 6, 7, 8, 9, DateTimeKind.Local);
            File.SetLastWriteTime(snapshotFile, createdAt);

            var newTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            await _store.UpdateEntriesAsync(directory, snapshot.ProfileId,
            [
                snapshot.Files["game.exe"] with { LastWriteTimeUtc = newTime },
                new FileMetadata("not-in-snapshot.txt", 1, newTime, false)
            ]);

            var loaded = (await _store.LoadAsync(directory, snapshot.ProfileId))!;

            Assert.Multiple(() =>
            {
                Assert.That(loaded.Data.Files["game.exe"].LastWriteTimeUtc, Is.EqualTo(newTime));
                Assert.That(loaded.Data.Files["game.exe"].Sha256, Is.EqualTo(snapshot.Files["game.exe"].Sha256));
                Assert.That(loaded.Data.Files.ContainsKey("not-in-snapshot.txt"), Is.False);
                Assert.That(loaded.CreatedAt, Is.EqualTo(createdAt));
            });
        }

        [Test]
        public async Task Backup_ChangedGameFiles_AfterSameVersionReinstall_SkipsIdenticalFiles_AndUpdatesTheSnapshot()
        {
            var profile = new BackupProfile { GameName = "Witcher", GameRootPath = _game };
            var directory = _paths.GetPaths().SnapshotsDirectory;
            await _store.SaveAsync(directory, await _service.CreateSnapshotAsync(profile.Id, _game));

            Rewrite("game.exe", "vanilla exe");
            Write("user.ini", "config");

            var runner = new BackupJobRunner(
                new ArchiveService(),
                new FileSystemProvider(),
                _service,
                _store,
                new IgnorePatternMatcher(),
                new FakeJobStateService(),
                _paths);
            var settings = AppSettings.Default(Path.Combine(_root, "drive"));
            var options = new BackupOptions(false, false, false, false, IncludeGameChanges: true);

            var first = await runner.RunBackupAsync([profile], options, settings);
            var stored = (await _store.LoadAsync(directory, profile.Id))!;
            var second = await runner.RunBackupAsync([profile], options, settings);

            Assert.Multiple(() =>
            {
                Assert.That(first.FilesArchived, Is.EqualTo(1), "only user.ini; game.exe has the vanilla content");
                Assert.That(first.Results.Single().Warnings, Has.Some.Contains("same content as in the snapshot"));
                Assert.That(stored.Data.Files["game.exe"].LastWriteTimeUtc, Is.EqualTo(File.GetLastWriteTimeUtc(Path.Combine(_game, "game.exe"))));
                Assert.That(second.Results.Single().Warnings, Has.None.Contains("same content as in the snapshot"), "the new timestamp was saved, so nothing is hashed again");
            });
        }
    }
}
