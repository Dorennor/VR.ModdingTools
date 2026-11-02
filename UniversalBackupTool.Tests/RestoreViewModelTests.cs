using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

using UniversalBackupTool.Model.Models.Backups;
using UniversalBackupTool.Model.Models.Profiles;
using UniversalBackupTool.Model.Models.Settings;
using UniversalBackupTool.Service.Services.Backups;
using UniversalBackupTool.Service.Services.Operations;
using UniversalBackupTool.Service.Services.Paths;
using UniversalBackupTool.Service.Services.Profiles;
using UniversalBackupTool.Service.Services.Settings;
using UniversalBackupTool.ViewModel.ViewModels.Restore;

namespace UniversalBackupTool.Tests
{
    [TestFixture]
    public class RestoreViewModelTests
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { Converters = { new JsonStringEnumConverter() } };

        private static readonly DateTimeOffset Older = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        private static readonly DateTimeOffset Newer = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

        private string _root = string.Empty;
        private string _backups = string.Empty;
        private FakeStoragePathsProvider _paths = null!;
        private OperationOutputService _output = null!;
        private OperationCoordinator _coordinator = null!;
        private FakeDialogs _dialogs = null!;
        private FakeVortexProcess _vortex = null!;

        [SetUp]
        public void SetUp()
        {
            _root = TestDirectoryFactory.CreateTemporaryDirectory();
            _backups = Path.Combine(_root, "Backups");
            _paths = new FakeStoragePathsProvider(_root);
            _output = new OperationOutputService();
            _coordinator = new OperationCoordinator(_output);
            _dialogs = new FakeDialogs();
            _vortex = new FakeVortexProcess();
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }

        private Task SaveSettingsAsync(string? backupRoot = null)
        {
            return new JsonSettingsStore().SaveAsync(_paths.GetPaths().SettingsFilePath, AppSettings.Default(backupRoot ?? _backups));
        }

        private async Task<RestoreViewModel> LoadAsync(params BackupProfile[] profiles)
        {
            await new JsonProfileStore().SaveAsync(_paths.GetPaths().ProfilesFilePath, profiles);

            var viewModel = new RestoreViewModel(
                new JsonProfileStore(),
                new JsonSettingsStore(),
                _paths,
                new RestoreService(new FakeSafetyBackupService()),
                _dialogs,
                _dialogs,
                new PathDriveService(new FakeDrives()),
                _vortex,
                _coordinator,
                _output);

            await viewModel.LoadCommand.ExecuteAsync(null);
            await viewModel.BackupListLoad;

            return viewModel;
        }

        private BackupProfile Game(string name) => new() { GameName = name, GameRootPath = Path.Combine(_root, "Installed", name) };

        private static async Task ChooseGameAsync(RestoreViewModel viewModel, string displayName)
        {
            viewModel.SelectedGame = viewModel.Games.Single(game => game.DisplayName == displayName);
            await viewModel.BackupListLoad;
        }

        /// <summary>
        /// Writes a dated backup folder the way the backup runner lays it out, with one archive holding <paramref name="fileName"/>.
        /// </summary>
        private string WriteBackup(string relativeFolder, DateTimeOffset createdAt, BackupComponent component = BackupComponent.GameConfigs, string gameName = "Skyrim", bool isComplete = true, string fileName = "settings.ini")
        {
            var folder = Path.Combine(_backups, relativeFolder);
            var archiveName = Path.GetFileName(folder) + ".zip";
            var archivesFolder = Path.Combine(Path.GetDirectoryName(folder)!, "_archives");

            Directory.CreateDirectory(folder);
            Directory.CreateDirectory(archivesFolder);

            using (var archive = ZipFile.Open(Path.Combine(archivesFolder, archiveName), ZipArchiveMode.Create))
            using (var writer = new StreamWriter(archive.CreateEntry(fileName).Open()))
                writer.Write("restored");

            var unit = new BackupManifestUnit(component, $"_archives/{archiveName}", string.Empty, Path.Combine(_root, "Original"), string.Empty) { ArchiveRootRelativePath = ".." };
            var manifest = new BackupManifest { GameName = gameName, CreatedAt = createdAt, IsComplete = isComplete, Units = [unit] };

            File.WriteAllText(Path.Combine(folder, BackupManifest.FileName), JsonSerializer.Serialize(manifest, JsonOptions));

            return folder;
        }

        [Test]
        public async Task Games_ListEveryProfileSorted_ThenVortexAppData_AndNothingIsChosen()
        {
            await SaveSettingsAsync();

            var viewModel = await LoadAsync(Game("Witcher 3"), Game("Fallout 4"));

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.Games.Select(game => game.DisplayName), Is.EqualTo(new[] { "Fallout 4", "Witcher 3", "Vortex AppData (all games)" }));
                Assert.That(viewModel.SelectedGame, Is.Null);
                Assert.That(viewModel.BackupListMessage, Is.EqualTo("Choose a game to see its backups."));
            });
        }

        [Test]
        public async Task Games_WithoutVortexGames_DoNotOfferVortexAppData()
        {
            await SaveSettingsAsync();

            var viewModel = await LoadAsync(new BackupProfile { GameName = "ESO", UsesVortex = false });

            Assert.That(viewModel.Games.Select(game => game.DisplayName), Is.EqualTo(new[] { "ESO" }));
        }

        [Test]
        public async Task Games_ListGamesThatAreNotInstalled_EvenWithOnlyInstalledGamesOn()
        {
            await new JsonSettingsStore().SaveAsync(_paths.GetPaths().SettingsFilePath, AppSettings.Default(_backups) with { ShowOnlyInstalledGames = true });

            var viewModel = await LoadAsync(Game("Not On This PC"));

            Assert.That(viewModel.Games.Select(game => game.DisplayName), Does.Contain("Not On This PC"));
        }

        [Test]
        public async Task ChoosingGame_ListsItsBackupsPerComponent_NewestFirst()
        {
            await SaveSettingsAsync();
            WriteBackup(@"Skyrim\configs\2026-10-01_12-00-00", Older);
            var newest = WriteBackup(@"Skyrim\configs\2026-11-01_12-00-00", Newer);
            WriteBackup(@"Skyrim\staging\2026-10-01_12-00-00", Older, BackupComponent.StagingFolders);
            WriteBackup(@"Fallout 4\configs\2026-11-01_12-00-00", Newer, gameName: "Fallout 4");

            var viewModel = await LoadAsync(Game("Skyrim"), Game("Fallout 4"));
            await ChooseGameAsync(viewModel, "Skyrim");

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.BackupGroups.Select(group => group.Title), Is.EqualTo(new[] { "Staging folders", "Game configs" }));
                Assert.That(viewModel.BackupGroups[1].Backups, Has.Count.EqualTo(2));
                Assert.That(viewModel.BackupGroups[1].Backups[0].Folder, Is.EqualTo(newest));
                Assert.That(viewModel.BackupGroups[1].Backups[0].PartsText, Is.EqualTo("1 part"));
                Assert.That(viewModel.HasBackupListMessage, Is.False);
            });
        }

        [Test]
        public async Task ChoosingGame_LeavesOutWhatCannotBeRestored()
        {
            await SaveSettingsAsync();
            var complete = WriteBackup(@"Skyrim\configs\2026-11-01_12-00-00", Newer);
            WriteBackup(@"Skyrim\configs\2026-11-02_12-00-00", Newer, isComplete: false);
            Directory.CreateDirectory(Path.Combine(_backups, @"Skyrim\configs\no-manifest"));
            var damaged = Path.Combine(_backups, @"Skyrim\configs\damaged");
            Directory.CreateDirectory(damaged);
            File.WriteAllText(Path.Combine(damaged, BackupManifest.FileName), "{ not json");

            var viewModel = await LoadAsync(Game("Skyrim"));
            await ChooseGameAsync(viewModel, "Skyrim");

            Assert.That(viewModel.BackupGroups.Single().Backups.Select(backup => backup.Folder), Is.EqualTo(new[] { complete }));
        }

        [Test]
        public async Task ChoosingGame_FindsOlderBackupsWithoutAComponentFolder_AndSeveralComponents()
        {
            await SaveSettingsAsync();
            var folder = WriteBackup(@"Skyrim\2024-01-31_12-00-00", Older);
            var manifestPath = Path.Combine(folder, BackupManifest.FileName);
            var manifest = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(manifestPath), JsonOptions)!;
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(
                manifest with { IsMonolithic = true, Units = [manifest.Units[0], manifest.Units[0] with { Component = BackupComponent.StagingFolders }] },
                JsonOptions));
            WriteBackup(@"Skyrim\configs\2026-11-01_12-00-00", Newer);

            var viewModel = await LoadAsync(Game("Skyrim"));
            await ChooseGameAsync(viewModel, "Skyrim");

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.BackupGroups.Select(group => group.Title), Is.EqualTo(new[] { "Game configs", "Several components" }));
                Assert.That(viewModel.BackupGroups[1].Backups.Single().Folder, Is.EqualTo(folder));
                Assert.That(viewModel.BackupGroups[1].Backups.Single().PartsText, Is.EqualTo("2 parts"));
            });
        }

        [Test]
        public async Task ChoosingVortexAppData_ListsTheSharedBackups()
        {
            await SaveSettingsAsync();
            var shared = WriteBackup(@"#Vortex AppData#\2026-11-01_12-00-00", Newer, BackupComponent.VortexAppData, VortexAppDataBackup.FolderName);

            var viewModel = await LoadAsync(Game("Skyrim"));
            await ChooseGameAsync(viewModel, "Vortex AppData (all games)");

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.BackupGroups.Single().Title, Is.EqualTo("Vortex AppData"));
                Assert.That(viewModel.BackupGroups.Single().Backups.Single().Folder, Is.EqualTo(shared));
            });
        }

        [Test]
        public async Task ChoosingGame_WithoutBackups_SaysSo()
        {
            await SaveSettingsAsync();

            var viewModel = await LoadAsync(Game("Skyrim"));
            await ChooseGameAsync(viewModel, "Skyrim");

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.BackupGroups, Is.Empty);
                Assert.That(viewModel.BackupListMessage, Does.StartWith("No backups of Skyrim found"));
            });
        }

        [Test]
        public async Task ChoosingGame_WithoutDestination_PointsToSettings()
        {
            await SaveSettingsAsync(string.Empty);

            var viewModel = await LoadAsync(Game("Skyrim"));
            await ChooseGameAsync(viewModel, "Skyrim");

            Assert.That(viewModel.BackupListMessage, Does.Contain("Settings"));
        }

        [Test]
        public async Task SelectBackup_OpensItsOptions_WithEveryComponentOnAgain()
        {
            await SaveSettingsAsync();
            var folder = WriteBackup(@"Skyrim\configs\2026-11-01_12-00-00", Newer);

            var viewModel = await LoadAsync(Game("Skyrim"));
            await ChooseGameAsync(viewModel, "Skyrim");
            viewModel.RestoreGameConfigs = false;

            Assert.That(viewModel.HasRestoreManifest, Is.False, "nothing is chosen before the button is used");

            viewModel.SelectBackupCommand.Execute(viewModel.BackupGroups.Single().Backups.Single());

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.HasRestoreManifest, Is.True);
                Assert.That(viewModel.RestoreFolderPath, Is.EqualTo(folder));
                Assert.That(viewModel.RestoreHasGameConfigs, Is.True);
                Assert.That(viewModel.RestoreHasStaging, Is.False);
                Assert.That(viewModel.RestoreGameConfigs, Is.True);
                Assert.That(viewModel.RestorePlanLines.Single(), Does.StartWith("Game configs: 1 archive part(s)").And.Contain(Path.Combine(_root, "Installed", "Skyrim")));
            });
        }

        [Test]
        public async Task ChoosingAnotherGame_ClosesTheOptions()
        {
            await SaveSettingsAsync();
            WriteBackup(@"Skyrim\configs\2026-11-01_12-00-00", Newer);

            var viewModel = await LoadAsync(Game("Skyrim"), Game("Fallout 4"));
            await ChooseGameAsync(viewModel, "Skyrim");
            viewModel.SelectBackupCommand.Execute(viewModel.BackupGroups.Single().Backups.Single());

            await ChooseGameAsync(viewModel, "Fallout 4");

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.HasRestoreManifest, Is.False);
                Assert.That(viewModel.RestoreFolderPath, Is.Null);
                Assert.That(viewModel.BackupGroups, Is.Empty);
            });
        }

        [Test]
        public async Task OpeningThePageAgain_KeepsTheChoice_AndFindsNewBackups()
        {
            await SaveSettingsAsync();
            var folder = WriteBackup(@"Skyrim\configs\2026-10-01_12-00-00", Older);

            var viewModel = await LoadAsync(Game("Skyrim"));
            await ChooseGameAsync(viewModel, "Skyrim");
            viewModel.SelectBackupCommand.Execute(viewModel.BackupGroups.Single().Backups.Single());

            WriteBackup(@"Skyrim\configs\2026-11-01_12-00-00", Newer);
            await viewModel.LoadCommand.ExecuteAsync(null);
            await viewModel.BackupListLoad;

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.SelectedGame?.GameName, Is.EqualTo("Skyrim"));
                Assert.That(viewModel.RestoreFolderPath, Is.EqualTo(folder));
                Assert.That(viewModel.BackupGroups.Single().Backups, Has.Count.EqualTo(2));
            });
        }

        [Test]
        public async Task OpeningThePageAgain_ClosesTheOptions_WhenTheBackupIsGone()
        {
            await SaveSettingsAsync();
            var folder = WriteBackup(@"Skyrim\configs\2026-10-01_12-00-00", Older);

            var viewModel = await LoadAsync(Game("Skyrim"));
            await ChooseGameAsync(viewModel, "Skyrim");
            viewModel.SelectBackupCommand.Execute(viewModel.BackupGroups.Single().Backups.Single());

            Directory.Delete(folder, recursive: true);
            await viewModel.LoadCommand.ExecuteAsync(null);
            await viewModel.BackupListLoad;

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.HasRestoreManifest, Is.False);
                Assert.That(viewModel.BackupGroups, Is.Empty);
            });
        }

        [Test]
        public async Task Restore_FromAListedBackup_ExtractsIntoTheProfileFolder()
        {
            await SaveSettingsAsync();
            WriteBackup(@"Skyrim\configs\2026-11-01_12-00-00", Newer);

            var viewModel = await LoadAsync(Game("Skyrim"));
            await ChooseGameAsync(viewModel, "Skyrim");
            viewModel.SelectBackupCommand.Execute(viewModel.BackupGroups.Single().Backups.Single());

            await viewModel.RestoreCommand.ExecuteAsync(null);

            var restored = Path.Combine(_root, "Installed", "Skyrim", "settings.ini");

            Assert.That(File.ReadAllText(restored), Is.EqualTo("restored"));
        }

        [Test]
        public async Task Restore_NotConfirmed_ChangesNothing()
        {
            await SaveSettingsAsync();
            WriteBackup(@"Skyrim\configs\2026-11-01_12-00-00", Newer);
            _dialogs.ConfirmResult = false;

            var viewModel = await LoadAsync(Game("Skyrim"));
            await ChooseGameAsync(viewModel, "Skyrim");
            viewModel.SelectBackupCommand.Execute(viewModel.BackupGroups.Single().Backups.Single());

            await viewModel.RestoreCommand.ExecuteAsync(null);

            Assert.That(Directory.Exists(Path.Combine(_root, "Installed", "Skyrim")), Is.False);
        }

        [Test]
        public async Task Browse_StillOpensABackupThatIsNotListed()
        {
            await SaveSettingsAsync();
            var folder = WriteBackup(@"Renamed Game\configs\2026-11-01_12-00-00", Newer, gameName: "Renamed Game");
            _dialogs.NextFolder = folder;

            var viewModel = await LoadAsync(Game("Skyrim"));
            await viewModel.BrowseRestoreFolderCommand.ExecuteAsync(null);

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.HasRestoreManifest, Is.True);
                Assert.That(viewModel.RestoreFolderPath, Is.EqualTo(folder));
                Assert.That(viewModel.RestoreSummary, Does.StartWith("Renamed Game"));
            });
        }
    }
}