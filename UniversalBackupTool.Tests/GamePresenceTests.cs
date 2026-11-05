using System.Diagnostics;

using UniversalBackupTool.Model.Models.Profiles;
using UniversalBackupTool.Model.Models.Snapshots;
using UniversalBackupTool.Service.Services.Profiles;
using UniversalBackupTool.Service.Services.Snapshots;

namespace UniversalBackupTool.Tests
{
    [TestFixture]
    public class GamePresenceTests
    {
        private const int RealGameBytes = 11 * 1024 * 1024;

        private string _root = string.Empty;
        private FakeStoragePathsProvider _paths = null!;

        [SetUp]
        public void SetUp()
        {
            _root = TestDirectoryFactory.CreateTemporaryDirectory();
            _paths = new FakeStoragePathsProvider(Path.Combine(_root, "app"));
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }

        private string File_(string relative, long sizeBytes)
        {
            var path = Path.Combine(_root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            using var stream = File.Create(path);
            stream.SetLength(sizeBytes);

            return path;
        }

        private string Game(string relative = "Game", long sizeBytes = RealGameBytes)
        {
            File_(Path.Combine(relative, "game.bin"), sizeBytes);

            return Path.Combine(_root, relative);
        }

        /// <summary>
        /// What a Steam uninstall often leaves in the folder.
        /// </summary>
        private string Leftovers(string relative = "Game")
        {
            File_(Path.Combine(relative, "logs", "game.log"), RealGameBytes);
            File_(Path.Combine(relative, "crash.dmp"), RealGameBytes);
            File_(Path.Combine(relative, "settings.ini"), 100);

            return Path.Combine(_root, relative);
        }

        private void SteamManifest(string library, string installDir) =>
            File.WriteAllText(
                File_(Path.Combine(library, "steamapps", $"appmanifest_{Guid.NewGuid():N}.acf"), 0),
                $"\"AppState\"\n{{\n\t\"appid\"\t\t\"1\"\n\t\"installdir\"\t\t\"{installDir}\"\n}}\n");

        private async Task SnapshotAsync(BackupProfile profile, params (string Path, long Size)[] files)
        {
            var entries = files.ToDictionary(
                file => file.Path,
                file => new FileMetadata(file.Path, file.Size, DateTime.UtcNow, false));

            await new JsonSnapshotStore().SaveAsync(_paths.GetPaths().SnapshotsDirectory, new SnapshotData(profile.Id, entries));
        }

        private async Task<GamePresence> CheckAsync(BackupProfile profile) =>
            (await new GamePresenceService(new JsonSnapshotStore(), _paths).CheckAsync([profile])).Single();

        [Test]
        public async Task MissingFolder_IsNotInstalled()
        {
            var presence = await CheckAsync(new BackupProfile { GameRootPath = Path.Combine(_root, "None") });

            Assert.Multiple(() =>
            {
                Assert.That(presence.IsInstalled, Is.False);
                Assert.That(presence.HasGameFolder, Is.False);
                Assert.That(presence.IsListed, Is.False);
            });
        }

        [Test]
        public async Task EmptyFolder_IsNotInstalled()
        {
            var game = Path.Combine(_root, "Game");
            Directory.CreateDirectory(game);

            Assert.That((await CheckAsync(new BackupProfile { GameRootPath = game })).IsInstalled, Is.False);
        }

        [Test]
        public async Task OnlyLogsAndSettings_AreLeftovers_HoweverLarge()
        {
            var presence = await CheckAsync(new BackupProfile { GameRootPath = Leftovers() });

            Assert.Multiple(() =>
            {
                Assert.That(presence.IsInstalled, Is.False);
                Assert.That(presence.HasGameFolder, Is.False);
            });
        }

        [Test]
        public async Task RealGameData_WithoutSteamOrSnapshot_IsInstalled()
        {
            Assert.That((await CheckAsync(new BackupProfile { GameRootPath = Game() })).IsInstalled, Is.True);
        }

        [Test]
        public async Task TinyFolder_WithoutSteamOrSnapshot_IsNotInstalled()
        {
            Assert.That((await CheckAsync(new BackupProfile { GameRootPath = Game(sizeBytes: 1024) })).IsInstalled, Is.False);
        }

        [Test]
        public async Task SteamManifest_MakesEvenATinyGameInstalled()
        {
            var game = Game(Path.Combine("Library", "steamapps", "common", "Tiny Game"), 10);
            SteamManifest("Library", "tiny game");

            Assert.That((await CheckAsync(new BackupProfile { GameRootPath = game })).IsInstalled, Is.True);
        }

        [Test]
        public async Task SteamManifest_AlsoCoversASubfolderOfTheGame()
        {
            var game = Game(Path.Combine("Library", "steamapps", "common", "Tiny Game", "bin"), 10);
            SteamManifest("Library", "Tiny Game");

            Assert.That((await CheckAsync(new BackupProfile { GameRootPath = game })).IsInstalled, Is.True);
        }

        [Test]
        public async Task SteamManifestOfAnotherGame_DoesNotCount()
        {
            var game = Leftovers(Path.Combine("Library", "steamapps", "common", "Uninstalled"));
            SteamManifest("Library", "Another Game");

            Assert.That((await CheckAsync(new BackupProfile { GameRootPath = game })).IsInstalled, Is.False);
        }

        [Test]
        public async Task NoSteamManifest_DoesNotHideARealGame_InASteamFolder()
        {
            var game = Game(Path.Combine("Library", "steamapps", "common", "Unpacked From A Backup"));

            Assert.That((await CheckAsync(new BackupProfile { GameRootPath = game })).IsInstalled, Is.True);
        }

        [Test]
        public async Task SteamManifest_DoesNotBringBackAFolderThatIsGone()
        {
            SteamManifest("Library", "Deleted By Hand");

            var presence = await CheckAsync(new BackupProfile { GameRootPath = Path.Combine(_root, "Library", "steamapps", "common", "Deleted By Hand") });

            Assert.That(presence.IsInstalled, Is.False);
        }

        [Test]
        public async Task Snapshot_VanillaFilesInPlace_IsInstalled_EvenIfSmall()
        {
            var game = Game(sizeBytes: 100);
            var profile = new BackupProfile { GameRootPath = game };
            await SnapshotAsync(profile, ("game.bin", 100));

            Assert.That((await CheckAsync(profile)).IsInstalled, Is.True);
        }

        [Test]
        public async Task Snapshot_LeftoverMods_DoNotCountAsTheGame()
        {
            // Files deployed by Vortex stay behind after an uninstall and can be large, but they are not vanilla.
            var game = Game(sizeBytes: RealGameBytes);
            var profile = new BackupProfile { GameRootPath = game };
            await SnapshotAsync(profile, ("Data/vanilla.ba2", 5000), ("game.exe", 100));

            Assert.That((await CheckAsync(profile)).IsInstalled, Is.False);
        }

        [Test]
        public async Task Snapshot_JudgesByBytesAndPaths_SoSmallLostFilesDoNotMatter()
        {
            File_(Path.Combine("Game", "Data", "big.ba2"), 10);
            var profile = new BackupProfile { GameRootPath = Path.Combine(_root, "Game") };
            await SnapshotAsync(profile, ("Data/big.ba2", 5000), ("a.dll", 100), ("b.dll", 100), ("c.dll", 100));

            Assert.That((await CheckAsync(profile)).IsInstalled, Is.True);
        }

        [Test]
        public async Task DamagedSnapshot_FallsBackToTheGameData()
        {
            var profile = new BackupProfile { GameRootPath = Game() };
            Directory.CreateDirectory(_paths.GetPaths().SnapshotsDirectory);
            await File.WriteAllTextAsync(Path.Combine(_paths.GetPaths().SnapshotsDirectory, $"{profile.Id}.json"), "{ not json");

            Assert.That((await CheckAsync(profile)).IsInstalled, Is.True);
        }

        [Test]
        public async Task StagingWithMods_KeepsAnUninstalledGameListed_WithoutItsGameFolder()
        {
            var staging = Path.GetDirectoryName(File_(Path.Combine("Staging", "Mod", "mod.esp"), 10))!;

            var presence = await CheckAsync(new BackupProfile { GameRootPath = Leftovers(), VortexStagingPath = staging });

            Assert.Multiple(() =>
            {
                Assert.That(presence.IsInstalled, Is.False);
                Assert.That(presence.HasGameFolder, Is.False);
                Assert.That(presence.HasStaging, Is.True);
                Assert.That(presence.IsListed, Is.True);
            });
        }

        [Test]
        public async Task EmptyStaging_IsNotAReasonToListAGame()
        {
            var staging = Path.Combine(_root, "Staging");
            Directory.CreateDirectory(staging);

            var presence = await CheckAsync(new BackupProfile { GameRootPath = Path.Combine(_root, "None"), VortexStagingPath = staging });

            Assert.That(presence.IsListed, Is.False);
        }

        [Test]
        public async Task StagingOfNonVortexProfile_DoesNotCount()
        {
            var staging = Path.GetDirectoryName(File_(Path.Combine("Staging", "mod.esp"), 10))!;

            var presence = await CheckAsync(new BackupProfile { UsesVortex = false, GameRootPath = Path.Combine(_root, "None"), VortexStagingPath = staging });

            Assert.Multiple(() =>
            {
                Assert.That(presence.HasStaging, Is.False);
                Assert.That(presence.IsListed, Is.False);
            });
        }

        [Test]
        public async Task CustomPaths_OnlyDecideWithoutGameAndStagingFolders()
        {
            var saves = Path.GetDirectoryName(File_(Path.Combine("Saves", "save.sav"), 10))!;

            var customOnly = await CheckAsync(new BackupProfile { CustomFolders = [saves] });
            var customGone = await CheckAsync(new BackupProfile { CustomFolders = [Path.Combine(_root, "None")] });
            var gameGone = await CheckAsync(new BackupProfile { GameRootPath = Path.Combine(_root, "None"), CustomFolders = [saves] });
            var noPaths = await CheckAsync(new BackupProfile());

            Assert.Multiple(() =>
            {
                Assert.That(customOnly.IsListed, Is.True);
                Assert.That(customOnly.HasCustomPaths, Is.True);
                Assert.That(customOnly.HasGameFolder, Is.False);
                Assert.That(customGone.IsListed, Is.False);
                Assert.That(gameGone.IsListed, Is.False);
                Assert.That(gameGone.HasCustomPaths, Is.True);
                Assert.That(noPaths.IsListed, Is.True);
            });
        }

        [Test]
        public void CheckFoldersExist_OnlyLooksAtExistence()
        {
            var service = new GamePresenceService(new JsonSnapshotStore(), _paths);
            var presence = service.CheckFoldersExist(new BackupProfile { GameRootPath = Leftovers(), VortexStagingPath = Path.Combine(_root, "None") });

            Assert.Multiple(() =>
            {
                Assert.That(presence.HasGameFolder, Is.True);
                Assert.That(presence.HasStaging, Is.False);
            });
        }

        [Test]
        public async Task EachCheck_SeesTheCurrentState_SoAReinstalledGameComesBack()
        {
            var game = Leftovers();
            var service = new GamePresenceService(new JsonSnapshotStore(), _paths);
            var profile = new BackupProfile { GameRootPath = game };

            Assert.That((await service.CheckAsync([profile])).Single().IsInstalled, Is.False);

            Game();

            Assert.That((await service.CheckAsync([profile])).Single().IsInstalled, Is.True);
        }

        [Test]
        public async Task GameData_DoesNotCountWhatAJunctionPointsTo()
        {
            var game = Game(sizeBytes: 100);
            var elsewhere = Game("Elsewhere");

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

            var presence = await CheckAsync(new BackupProfile { GameRootPath = game });

            // A plain delete must not follow the junction in tear down.
            Directory.Delete(Path.Combine(game, "link"));

            Assert.That(presence.IsInstalled, Is.False);
        }
    }
}