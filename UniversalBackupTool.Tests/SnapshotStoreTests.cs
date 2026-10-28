using UniversalBackupTool.Model.Models.Snapshots;
using UniversalBackupTool.Service.Services.Snapshots;

namespace UniversalBackupTool.Tests
{
    [TestFixture]
    public class SnapshotStoreTests
    {
        private string _directory = string.Empty;
        private JsonSnapshotStore _store = null!;

        [SetUp]
        public void SetUp()
        {
            _directory = Path.Combine(Path.GetTempPath(), "ubt-snapshots-" + Guid.NewGuid().ToString("N"));
            _store = new JsonSnapshotStore();
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }

        private static SnapshotData CreateSnapshot(Guid id)
        {
            return new SnapshotData(id, new Dictionary<string, FileMetadata>(StringComparer.OrdinalIgnoreCase)
            {
                ["Data\\a.esp"] = new FileMetadata("Data\\a.esp", 10, new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc), false),
                ["game.exe"] = new FileMetadata("game.exe", 20, new DateTime(2024, 2, 3, 4, 5, 6, DateTimeKind.Utc), true)
            });
        }

        [Test]
        public async Task Load_MissingFile_ReturnsNull()
        {
            Assert.That(await _store.LoadAsync(_directory, Guid.NewGuid()), Is.Null);
        }

        [Test]
        public async Task SaveThenLoad_RoundTripsFiles()
        {
            var id = Guid.NewGuid();
            var snapshot = CreateSnapshot(id);

            await _store.SaveAsync(_directory, snapshot);
            var loaded = await _store.LoadAsync(_directory, id);

            Assert.That(loaded, Is.Not.Null);
            Assert.That(loaded!.Data.ProfileId, Is.EqualTo(id));
            Assert.That(loaded.Data.Files, Is.EquivalentTo(snapshot.Files));
        }

        [Test]
        public async Task Load_KeysAreCaseInsensitive()
        {
            var id = Guid.NewGuid();
            await _store.SaveAsync(_directory, CreateSnapshot(id));

            var loaded = await _store.LoadAsync(_directory, id);

            Assert.That(loaded!.Data.Files.ContainsKey("GAME.EXE"), Is.True);
        }

        [Test]
        public async Task Save_Overwrites_AndLeavesNoTempFile()
        {
            var id = Guid.NewGuid();
            await _store.SaveAsync(_directory, CreateSnapshot(id));
            await _store.SaveAsync(_directory, new SnapshotData(id, new Dictionary<string, FileMetadata>()));

            var loaded = await _store.LoadAsync(_directory, id);

            Assert.That(loaded!.Data.Files, Is.Empty);
            Assert.That(Directory.GetFiles(_directory, "*.tmp"), Is.Empty);
        }

        [Test]
        public async Task Snapshots_AreSeparatePerProfile()
        {
            var first = Guid.NewGuid();
            var second = Guid.NewGuid();

            await _store.SaveAsync(_directory, CreateSnapshot(first));

            Assert.That(await _store.LoadAsync(_directory, second), Is.Null);
        }

        [Test]
        public async Task Load_CorruptedFile_ReturnsNull()
        {
            var id = Guid.NewGuid();
            Directory.CreateDirectory(_directory);
            await File.WriteAllTextAsync(Path.Combine(_directory, id.ToString("D") + ".json"), "{ not json");

            Assert.That(await _store.LoadAsync(_directory, id), Is.Null);
        }

        [Test]
        public void Save_EmptyDirectory_Throws()
        {
            Assert.That(async () => await _store.SaveAsync("", CreateSnapshot(Guid.NewGuid())), Throws.ArgumentException);
        }
    }
}