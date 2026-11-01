using UniversalBackupTool.Model.Models.Profiles;
using UniversalBackupTool.Service.Services.Profiles;

namespace UniversalBackupTool.Tests
{
    [TestFixture]
    public class ProfileStoreTests
    {
        private string _tempDirectory = string.Empty;

        [SetUp]
        public void SetUp()
        {
            _tempDirectory = Path.Combine(Path.GetTempPath(), "UBT_ProfileTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDirectory);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_tempDirectory))
                Directory.Delete(_tempDirectory, recursive: true);
        }

        private static BackupProfile CreateProfile(string name = "Skyrim", string? staging = @"D:\Mods\skyrim", string? root = @"D:\Games\Skyrim", params string[] custom)
        {
            return new BackupProfile
            {
                GameName = name,
                VortexStagingPath = staging,
                GameRootPath = root,
                CustomFolders = custom.ToList()
            };
        }

        [Test]
        public async Task Load_FileMissing_ReturnsEmptyList()
        {
            var profiles = await new JsonProfileStore().LoadAsync(Path.Combine(_tempDirectory, "profiles.json"));

            Assert.That(profiles, Is.Empty);
        }

        [Test]
        public async Task Load_FileMissing_DoesNotCreateFile()
        {
            var path = Path.Combine(_tempDirectory, "profiles.json");

            await new JsonProfileStore().LoadAsync(path);

            Assert.That(File.Exists(path), Is.False);
        }

        [Test]
        public void Load_EmptyPath_Throws()
        {
            Assert.That(async () => await new JsonProfileStore().LoadAsync(" "), Throws.ArgumentException);
        }

        [Test]
        public void Save_EmptyPath_Throws()
        {
            Assert.That(async () => await new JsonProfileStore().SaveAsync("", []), Throws.ArgumentException);
        }

        [Test]
        public async Task SaveThenLoad_RoundTripsAllFields()
        {
            var store = new JsonProfileStore();
            var path = Path.Combine(_tempDirectory, "profiles.json");
            var original = CreateProfile("ESO", null, null, @"C:\Docs\Addons", @"C:\Docs\Other") with
            {
                UseIgnoreList = false
            };

            await store.SaveAsync(path, [original]);
            var loaded = await store.LoadAsync(path);

            Assert.That(loaded, Has.Count.EqualTo(1));
            var profile = loaded[0];
            Assert.That(profile.Id, Is.EqualTo(original.Id));
            Assert.That(profile.GameName, Is.EqualTo("ESO"));
            Assert.That(profile.VortexStagingPath, Is.Null);
            Assert.That(profile.GameRootPath, Is.Null);
            Assert.That(profile.CustomFolders, Is.EqualTo(new[] { @"C:\Docs\Addons", @"C:\Docs\Other" }));
            Assert.That(profile.UseIgnoreList, Is.False);
        }

        [Test]
        public async Task Load_ProfilesWithTheOldPerProfileAppDataPath_StillLoad_AndSaveDropsIt()
        {
            var store = new JsonProfileStore();
            var path = Path.Combine(_tempDirectory, "profiles.json");

            // profiles.json as written by the version with a Vortex AppData folder per profile.
            await File.WriteAllTextAsync(path, """
                [
                  {
                    "Id": "d3e61fbe-494e-4d15-9462-92d6985bb0a2",
                    "GameName": "Valheim",
                    "UsesVortex": true,
                    "VortexStagingPath": "E:\\Vortex Mods\\valheim",
                    "GameRootPath": "E:\\Games\\Valheim",
                    "VortexAppDataPath": "C:\\Users\\Test\\AppData\\Roaming\\Vortex",
                    "CustomFolders": [],
                    "UseIgnoreList": true,
                    "CreatedAtUtc": "2026-11-01T03:55:40.2357754+02:00"
                  }
                ]
                """);

            var loaded = await store.LoadAsync(path);
            await store.SaveAsync(path, loaded);
            var saved = await File.ReadAllTextAsync(path);

            Assert.Multiple(() =>
            {
                Assert.That(loaded.Single().Id, Is.EqualTo(Guid.Parse("d3e61fbe-494e-4d15-9462-92d6985bb0a2")));
                Assert.That(loaded.Single().VortexStagingPath, Is.EqualTo(@"E:\Vortex Mods\valheim"));
                Assert.That(loaded.Single().GameRootPath, Is.EqualTo(@"E:\Games\Valheim"));
                Assert.That(saved, Does.Not.Contain("VortexAppDataPath"));
            });
        }

        [Test]
        public async Task Save_CreatesMissingDirectory()
        {
            var path = Path.Combine(_tempDirectory, "nested", "deeper", "profiles.json");

            await new JsonProfileStore().SaveAsync(path, [CreateProfile()]);

            Assert.That(File.Exists(path), Is.True);
        }

        [Test]
        public async Task Save_OverwritesExistingFile_AndLeavesNoTempFile()
        {
            var store = new JsonProfileStore();
            var path = Path.Combine(_tempDirectory, "profiles.json");

            await store.SaveAsync(path, [CreateProfile("First"), CreateProfile("Second")]);
            await store.SaveAsync(path, [CreateProfile("Only")]);

            var loaded = await store.LoadAsync(path);

            Assert.That(loaded.Select(p => p.GameName), Is.EqualTo(new[] { "Only" }));
            Assert.That(File.Exists(path + ".tmp"), Is.False);
        }

        [Test]
        public async Task Save_EmptyList_LoadsAsEmpty()
        {
            var store = new JsonProfileStore();
            var path = Path.Combine(_tempDirectory, "profiles.json");

            await store.SaveAsync(path, []);

            Assert.That(await store.LoadAsync(path), Is.Empty);
        }

        [Test]
        public async Task Load_JsonNull_ReturnsEmptyList()
        {
            var path = Path.Combine(_tempDirectory, "profiles.json");
            await File.WriteAllTextAsync(path, "null");

            Assert.That(await new JsonProfileStore().LoadAsync(path), Is.Empty);
        }

        [Test]
        public async Task Save_PreservesProfileOrder()
        {
            var store = new JsonProfileStore();
            var path = Path.Combine(_tempDirectory, "profiles.json");

            await store.SaveAsync(path, [CreateProfile("C"), CreateProfile("A"), CreateProfile("B")]);

            var loaded = await store.LoadAsync(path);

            Assert.That(loaded.Select(p => p.GameName), Is.EqualTo(new[] { "C", "A", "B" }));
        }
    }
}