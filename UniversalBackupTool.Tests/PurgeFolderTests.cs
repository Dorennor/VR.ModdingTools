using UniversalBackupTool.Model.Models.Snapshots;
using UniversalBackupTool.Service.Services.Snapshots;
using UniversalBackupTool.Services;

namespace UniversalBackupTool.Tests
{
    /// <summary>
    /// Purge removes the folders mods added once their files are gone, and keeps every vanilla folder, also empty ones.
    /// </summary>
    [TestFixture]
    public class PurgeFolderTests
    {
        private string _root = string.Empty;
        private string _game = string.Empty;
        private SnapshotService _service = null!;

        [SetUp]
        public void SetUp()
        {
            _root = TestDirectoryFactory.CreateTemporaryDirectory();
            _game = Path.Combine(_root, "game");
            _service = new SnapshotService(new FileSystemProvider(), new FakeSafetyBackupService());

            Write("game.exe");
            Write("Data/main.bsa");
            Directory.CreateDirectory(Path.Combine(_game, "Data", "Video"));   // vanilla, empty
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }

        private void Write(string relative)
        {
            var path = Path.Combine(_game, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, relative);
        }

        private bool FolderExists(string relative) => Directory.Exists(Path.Combine(_game, relative.Replace('/', Path.DirectorySeparatorChar)));

        private void AddModFiles()
        {
            Write("Data/textures/mod/a.dds");
            Write("Data/meshes/b.nif");
            Write("SKSE/Plugins/plugin.dll");
            Directory.CreateDirectory(Path.Combine(_game, "Data", "leftover", "empty"));   // already empty, left by an older purge
        }

        [Test]
        public async Task CreateSnapshot_RecordsFolders_IncludingEmptyOnes()
        {
            var snapshot = await _service.CreateSnapshotAsync(Guid.NewGuid(), _game);

            Assert.That(snapshot.Directories, Is.EquivalentTo(new[] { "Data", "Data/Video" }));
        }

        [Test]
        public async Task Purge_RemovesAddedFolders_DeepestFirst_AndKeepsVanillaFolders()
        {
            var snapshot = await _service.CreateSnapshotAsync(Guid.NewGuid(), _game);
            AddModFiles();

            var preview = (await _service.GetUnregisteredDirectoriesAsync(_game, snapshot)).ToList();
            var result = await _service.PurgeUnregisteredFilesAsync(_game, snapshot);

            Assert.Multiple(() =>
            {
                Assert.That(preview, Is.EquivalentTo(new[] { "Data/textures", "Data/textures/mod", "Data/meshes", "SKSE", "SKSE/Plugins", "Data/leftover", "Data/leftover/empty" }));
                Assert.That(preview.IndexOf("Data/textures/mod"), Is.LessThan(preview.IndexOf("Data/textures")), "deepest first");
                Assert.That(result.FilesDeleted, Is.EqualTo(3));
                Assert.That(result.FoldersRemoved, Is.EqualTo(7));
                Assert.That(result.FoldersKept, Is.Zero);
                Assert.That(FolderExists("Data/textures"), Is.False);
                Assert.That(FolderExists("SKSE"), Is.False);
                Assert.That(FolderExists("Data/leftover"), Is.False);
                Assert.That(FolderExists("Data"), Is.True);
                Assert.That(FolderExists("Data/Video"), Is.True, "vanilla folder, empty in the snapshot");
            });
        }

        [Test]
        public async Task Purge_KeepsAFolderThatStillHasContent()
        {
            var snapshot = await _service.CreateSnapshotAsync(Guid.NewGuid(), _game);
            Write("Mods/locked.dat");
            var lockedPath = Path.Combine(_game, "Mods", "locked.dat");

            using (new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                try
                {
                    var result = await _service.PurgeUnregisteredFilesAsync(_game, snapshot);
                    Assert.That(result.FoldersKept, Is.EqualTo(1));
                }
                catch (IOException)
                {
                    // Deleting a locked file throws before folders are handled; the folder obviously stays then.
                }
            }

            Assert.That(FolderExists("Mods"), Is.True);
        }

        [Test]
        public async Task Purge_WithSnapshotWithoutFolders_KeepsFoldersHoldingVanillaFiles()
        {
            var snapshot = await _service.CreateSnapshotAsync(Guid.NewGuid(), _game);
            var legacy = new SnapshotData(snapshot.ProfileId, snapshot.Files);   // older snapshot: Directories is null
            AddModFiles();

            var result = await _service.PurgeUnregisteredFilesAsync(_game, legacy);

            Assert.Multiple(() =>
            {
                Assert.That(FolderExists("Data"), Is.True, "holds vanilla files");
                Assert.That(FolderExists("Data/textures"), Is.False);
                Assert.That(FolderExists("Data/leftover"), Is.False);
                Assert.That(FolderExists("Data/Video"), Is.False, "an empty vanilla folder is unknown to an older snapshot");
                Assert.That(result.FoldersRemoved, Is.EqualTo(8));
            });
        }

        [Test]
        public async Task SnapshotStore_RoundTripsFolders()
        {
            var directory = Path.Combine(_root, "snapshots");
            var store = new JsonSnapshotStore();
            var snapshot = await _service.CreateSnapshotAsync(Guid.NewGuid(), _game);

            await store.SaveAsync(directory, snapshot);
            var loaded = (await store.LoadAsync(directory, snapshot.ProfileId))!;

            Assert.That(loaded.Data.Directories, Is.EquivalentTo(snapshot.Directories!));
        }
    }
}