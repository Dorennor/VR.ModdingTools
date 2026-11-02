using System.Diagnostics;
using System.Text.Json;

using UniversalBackupTool.Model.Models.Profiles;
using UniversalBackupTool.Service.Services.Profiles;

namespace UniversalBackupTool.Tests
{
    [TestFixture]
    public class GamePresenceTests
    {
        private string _root = string.Empty;
        private string _sizesFile = string.Empty;

        [SetUp]
        public void SetUp()
        {
            _root = TestDirectoryFactory.CreateTemporaryDirectory();
            _sizesFile = Path.Combine(_root, "game_sizes.json");
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }

        private string Folder(string name, int sizeBytes)
        {
            var folder = Path.Combine(_root, name);
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "file.bin"), new byte[sizeBytes]);

            return folder;
        }

        /// <summary>
        /// A new service instance, as in the next application run: it measures again and only knows the remembered sizes.
        /// </summary>
        private async Task<GamePresenceState> CheckAsync(BackupProfile profile) =>
            (await new GamePresenceService().CheckAsync([profile], _sizesFile)).Single().State;

        [Test]
        public async Task FirstCheck_AcceptsExistingFolder_AndRemembersItsSize()
        {
            var game = Folder("Game", 1000);

            var state = await CheckAsync(new BackupProfile { GameRootPath = game });

            var remembered = JsonSerializer.Deserialize<Dictionary<string, long>>(await File.ReadAllTextAsync(_sizesFile))!;

            Assert.Multiple(() =>
            {
                Assert.That(state, Is.EqualTo(GamePresenceState.OnDisk));
                Assert.That(remembered[game], Is.EqualTo(1000));
            });
        }

        [TestCase(600, GamePresenceState.OnDisk, TestName = "SlightlySmaller_IsStillOnDisk")]
        [TestCase(5000, GamePresenceState.OnDisk, TestName = "Larger_IsStillOnDisk")]
        [TestCase(400, GamePresenceState.TooSmall, TestName = "BelowHalf_IsTooSmall")]
        public async Task SizeChange(int newSize, GamePresenceState expected)
        {
            var profile = new BackupProfile { GameRootPath = Folder("Game", 1000) };
            await CheckAsync(profile);

            Folder("Game", newSize);

            Assert.That(await CheckAsync(profile), Is.EqualTo(expected));
        }

        [Test]
        public async Task TooSmall_KeepsTheRememberedSize_SoReinstallingBringsTheGameBack()
        {
            var profile = new BackupProfile { GameRootPath = Folder("Game", 1000) };
            await CheckAsync(profile);

            Folder("Game", 100);
            Assert.That(await CheckAsync(profile), Is.EqualTo(GamePresenceState.TooSmall));
            Assert.That(await CheckAsync(profile), Is.EqualTo(GamePresenceState.TooSmall));

            Folder("Game", 1000);
            Assert.That(await CheckAsync(profile), Is.EqualTo(GamePresenceState.OnDisk));
        }

        [Test]
        public async Task MissingFolder_KeepsTheRememberedSize_LikeADisconnectedDrive()
        {
            var game = Folder("Game", 1000);
            var profile = new BackupProfile { GameRootPath = game };
            await CheckAsync(profile);

            Directory.Delete(game, recursive: true);
            Assert.That(await CheckAsync(profile), Is.EqualTo(GamePresenceState.Missing));

            Folder("Game", 100);
            Assert.That(await CheckAsync(profile), Is.EqualTo(GamePresenceState.TooSmall));
        }

        [Test]
        public async Task EmptyFolder_IsMissing()
        {
            var game = Path.Combine(_root, "Game");
            Directory.CreateDirectory(game);

            Assert.That(await CheckAsync(new BackupProfile { GameRootPath = game }), Is.EqualTo(GamePresenceState.Missing));
        }

        [Test]
        public async Task EitherFolderIsEnough_GameUninstalledButStagingStillThere()
        {
            var staging = Folder("Staging", 1000);
            var profile = new BackupProfile { GameRootPath = Path.Combine(_root, "Uninstalled"), VortexStagingPath = staging };

            Assert.That(await CheckAsync(profile), Is.EqualTo(GamePresenceState.OnDisk));
            Assert.That(new GamePresenceService().FoldersExist(profile), Is.True);
        }

        [Test]
        public async Task StagingOfNonVortexProfile_DoesNotCount()
        {
            var profile = new BackupProfile
            {
                UsesVortex = false,
                GameRootPath = Path.Combine(_root, "Uninstalled"),
                VortexStagingPath = Folder("Staging", 1000)
            };

            Assert.That(await CheckAsync(profile), Is.EqualTo(GamePresenceState.Missing));
        }

        [Test]
        public async Task CustomPaths_OnlyDecideWithoutGameAndStagingFolders()
        {
            var saves = Folder("Saves", 10);

            Assert.That(await CheckAsync(new BackupProfile { CustomFolders = [saves] }), Is.EqualTo(GamePresenceState.OnDisk));
            Assert.That(await CheckAsync(new BackupProfile { CustomFolders = [Path.Combine(_root, "None")] }), Is.EqualTo(GamePresenceState.Missing));
            Assert.That(await CheckAsync(new BackupProfile { GameRootPath = Path.Combine(_root, "None"), CustomFolders = [saves] }), Is.EqualTo(GamePresenceState.Missing));
            Assert.That(await CheckAsync(new BackupProfile()), Is.EqualTo(GamePresenceState.OnDisk));
        }

        [Test]
        public async Task AcceptCurrentSizes_MakesASmallerGameTheNewNormal()
        {
            var profile = new BackupProfile { GameRootPath = Folder("Game", 1000) };
            var service = new GamePresenceService();
            await service.CheckAsync([profile], _sizesFile);

            // The same run measured 1000 already, so this also proves that accepting measures again.
            Folder("Game", 100);
            await service.AcceptCurrentSizesAsync([profile], _sizesFile);

            Assert.That((await service.CheckAsync([profile], _sizesFile)).Single().State, Is.EqualTo(GamePresenceState.OnDisk));
            Assert.That(await CheckAsync(profile), Is.EqualTo(GamePresenceState.OnDisk));
        }

        [Test]
        public async Task SizesOfRemovedProfiles_AreForgotten()
        {
            var first = Folder("First", 100);
            var second = Folder("Second", 100);
            var firstProfile = new BackupProfile { GameRootPath = first };
            var secondProfile = new BackupProfile { GameRootPath = second };

            await new GamePresenceService().CheckAsync([firstProfile, secondProfile], _sizesFile);
            await new GamePresenceService().CheckAsync([secondProfile], _sizesFile);

            var remembered = JsonSerializer.Deserialize<Dictionary<string, long>>(await File.ReadAllTextAsync(_sizesFile))!;

            Assert.That(remembered.Keys, Is.EqualTo(new[] { second }));
        }

        [Test]
        public async Task DamagedSizesFile_IsStartedOver()
        {
            var profile = new BackupProfile { GameRootPath = Folder("Game", 100) };
            await File.WriteAllTextAsync(_sizesFile, "{ not json");

            Assert.That(await CheckAsync(profile), Is.EqualTo(GamePresenceState.OnDisk));
            Assert.That(JsonSerializer.Deserialize<Dictionary<string, long>>(await File.ReadAllTextAsync(_sizesFile)), Has.Count.EqualTo(1));
        }

        [Test]
        public async Task Size_DoesNotCountWhatAJunctionPointsTo()
        {
            var game = Folder("Game", 1000);
            var elsewhere = Folder("Elsewhere", 100000);

            using (var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{Path.Combine(game, "link")}\" \"{elsewhere}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true
            })!)
            {
                await process.WaitForExitAsync();

                if (process.ExitCode != 0)
                    Assert.Ignore("Junctions are not supported here.");
            }

            await CheckAsync(new BackupProfile { GameRootPath = game });

            var remembered = JsonSerializer.Deserialize<Dictionary<string, long>>(await File.ReadAllTextAsync(_sizesFile))!;

            Assert.That(remembered[game], Is.EqualTo(1000));

            // A plain delete must not follow the junction in tear down.
            Directory.Delete(Path.Combine(game, "link"));
        }
    }
}