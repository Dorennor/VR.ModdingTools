using UniversalBackupTool.Interface.Interfaces.Appearance;
using UniversalBackupTool.Interface.Interfaces.Backups;
using UniversalBackupTool.Interface.Interfaces.Dialogs;
using UniversalBackupTool.Interface.Interfaces.Paths;
using UniversalBackupTool.Interface.Interfaces.Vortex;
using UniversalBackupTool.Model.Models.Backups;
using UniversalBackupTool.Model.Models.Jobs;
using UniversalBackupTool.Model.Models.Operations;
using UniversalBackupTool.Model.Models.Profiles;
using UniversalBackupTool.Model.Models.Settings;
using UniversalBackupTool.Service.Services.Backups;
using UniversalBackupTool.Service.Services.Operations;
using UniversalBackupTool.Service.Services.Paths;
using UniversalBackupTool.Service.Services.Profiles;
using UniversalBackupTool.Service.Services.Settings;
using UniversalBackupTool.ViewModel.ViewModels.BackupRestore;
using UniversalBackupTool.ViewModel.ViewModels.Dashboard;
using UniversalBackupTool.ViewModel.ViewModels.Settings;

namespace UniversalBackupTool.Tests
{
    internal sealed class FakeBackupJobRunner : IBackupJobRunner
    {
        public Func<IReadOnlyList<BackupProfile>, BackupOptions, BackupBatchResult> OnRun { get; set; } =
            (profiles, _) => new BackupBatchResult(
                profiles.Select(profile => new BackupResult(profile.GameName, "dir", 1, [], [])).ToList(),
                [],
                profiles.Count);

        public int RunCount { get; private set; }

        public BackupOptions? LastOptions { get; private set; }

        public Task<BackupBatchResult> RunBackupAsync(IReadOnlyList<BackupProfile> profiles, BackupOptions options, AppSettings settings, IProgress<BackupProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            RunCount++;
            LastOptions = options;
            return Task.FromResult(OnRun(profiles, options));
        }

        public Task<BackupBatchResult> ResumeBackupAsync(ActiveBackupJob job, AppSettings settings, IProgress<BackupProgress>? progress = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new BackupBatchResult([], [], 0));
    }

    internal sealed class FakeDialogs : IFolderPickerService, IFilePickerService, IConfirmationService
    {
        public string? NextFolder { get; set; }

        public List<string> NextFiles { get; } = [];

        public bool ConfirmResult { get; set; } = true;

        public string? PickFolder(string? initialPath = null) => NextFolder;

        public IReadOnlyList<string> PickFiles(string? initialDirectory = null) => NextFiles.ToList();

        public bool Confirm(string title, string message) => ConfirmResult;
    }

    internal sealed class FakeThemeService : IThemeService
    {
        public List<AppTheme> Applied { get; } = [];

        public void Apply(AppTheme theme) => Applied.Add(theme);
    }

    internal sealed class FakeVortexProcess : IVortexProcessService
    {
        public bool IsRunning { get; set; }

        public bool IsVortexRunning() => IsRunning;
    }

    internal sealed class FakeDrives : ISystemPathsProvider
    {
        public IReadOnlyList<string> GetAvailableDrives() => ["C:", "D:"];

        public bool PathExists(string path) => false;
    }

    [TestFixture]
    public class ViewModelTests
    {
        private string _root = string.Empty;
        private FakeStoragePathsProvider _paths = null!;
        private OperationOutputService _output = null!;
        private OperationCoordinator _coordinator = null!;
        private FakeDialogs _dialogs = null!;
        private FakeBackupJobRunner _runner = null!;
        private FakeVortexProcess _vortex = null!;

        [SetUp]
        public void SetUp()
        {
            _root = TestDirectoryFactory.CreateTemporaryDirectory();
            _paths = new FakeStoragePathsProvider(_root);
            _output = new OperationOutputService();
            _coordinator = new OperationCoordinator(_output);
            _dialogs = new FakeDialogs();
            _runner = new FakeBackupJobRunner();
            _vortex = new FakeVortexProcess();
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }

        private async Task SaveProfilesAsync(params string[] names)
        {
            var profiles = names.Select(name => new BackupProfile { GameName = name, VortexStagingPath = Path.Combine(_root, name) }).ToList();
            await new JsonProfileStore().SaveAsync(_paths.GetPaths().ProfilesFilePath, profiles);
        }

        private async Task<BackupRestoreViewModel> CreateBackupRestoreAsync(params string[] games)
        {
            await SaveProfilesAsync(games);

            return await LoadBackupRestoreAsync();
        }

        private async Task<BackupRestoreViewModel> LoadBackupRestoreAsync()
        {
            var viewModel = new BackupRestoreViewModel(
                new JsonProfileStore(),
                new JsonSettingsStore(),
                _paths,
                _runner,
                new FakeJobStateService(),
                new RestoreService(new FakeSafetyBackupService()),
                _dialogs,
                _dialogs,
                new PathDriveService(new FakeDrives()),
                _vortex,
                new GamePresenceService(),
                _coordinator,
                _output);

            await viewModel.LoadCommand.ExecuteAsync(null);
            return viewModel;
        }

        private async Task EnableOnlyInstalledGamesAsync()
        {
            var settingsPath = _paths.GetPaths().SettingsFilePath;

            await new JsonSettingsStore().SaveAsync(settingsPath, AppSettings.Default(Path.Combine(_root, "Backups")) with { ShowOnlyInstalledGames = true });
        }

        private string CreateGameFolder(string name, int sizeBytes)
        {
            var folder = Path.Combine(_root, name);
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "game.bin"), new byte[sizeBytes]);

            return folder;
        }

        [Test]
        public async Task OnlyInstalledGames_Off_ListsGamesWithoutFolders()
        {
            var viewModel = await CreateBackupRestoreAsync("Skyrim", "Fallout 4");

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.VortexTargets.VisibleTargets, Has.Count.EqualTo(2));
                Assert.That(viewModel.HasHiddenGamesSummary, Is.False);
                Assert.That(File.Exists(_paths.GetPaths().GameSizesFilePath), Is.False);
            });
        }

        [Test]
        public async Task OnlyInstalledGames_On_HidesMissingGames_ButRestoreKeepsAllProfiles()
        {
            CreateGameFolder("Skyrim", 100);
            await EnableOnlyInstalledGamesAsync();

            var viewModel = await CreateBackupRestoreAsync("Skyrim", "Fallout 4");
            await viewModel.InstalledGamesCheck;

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.VortexTargets.VisibleTargets.Select(target => target.GameName), Is.EqualTo(new[] { "Skyrim" }));
                Assert.That(viewModel.VortexTargets.Summary, Is.EqualTo("0 of 1 selected"));
                Assert.That(viewModel.Targets, Has.Count.EqualTo(2));
                Assert.That(viewModel.HiddenGamesSummary, Does.Contain("Fallout 4"));
                Assert.That(viewModel.HasShrunkGames, Is.False);
            });
        }

        [Test]
        public async Task OnlyInstalledGames_NoGameInstalled_ExplainsTheEmptyList()
        {
            await EnableOnlyInstalledGamesAsync();

            var viewModel = await CreateBackupRestoreAsync("Skyrim");
            await viewModel.InstalledGamesCheck;

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.HasNoVisibleTargets, Is.True);
                Assert.That(viewModel.NoVisibleTargetsText, Does.Contain("Show only installed games"));
            });
        }

        [Test]
        public async Task OnlyInstalledGames_GameGone_IsDeselectedOnReload_AndNotBackedUp()
        {
            var skyrim = CreateGameFolder("Skyrim", 100);
            CreateGameFolder("Fallout 4", 100);
            await EnableOnlyInstalledGamesAsync();

            var viewModel = await CreateBackupRestoreAsync("Skyrim", "Fallout 4");
            await viewModel.InstalledGamesCheck;
            viewModel.VortexTargets.ToggleSelectAllCommand.Execute(null);

            Directory.Delete(skyrim, recursive: true);
            await viewModel.LoadCommand.ExecuteAsync(null);
            await viewModel.InstalledGamesCheck;

            IReadOnlyList<string> backedUp = [];
            _runner.OnRun = (profiles, _) =>
            {
                backedUp = profiles.Select(profile => profile.GameName).ToList();
                return new BackupBatchResult([], [], 0);
            };
            viewModel.IncludeStaging = true;
            await viewModel.StartBackupCommand.ExecuteAsync(null);

            Assert.That(backedUp, Is.EqualTo(new[] { "Fallout 4" }));
        }

        [Test]
        public async Task OnlyInstalledGames_ShrunkGame_IsHiddenUntilItsSizeIsAccepted()
        {
            var skyrim = CreateGameFolder("Skyrim", 1000);
            await EnableOnlyInstalledGamesAsync();

            var first = await CreateBackupRestoreAsync("Skyrim");
            await first.InstalledGamesCheck;
            Assert.That(first.VortexTargets.VisibleTargets, Has.Count.EqualTo(1));

            // The next application run: a new service measures again and compares with the remembered size.
            File.WriteAllBytes(Path.Combine(skyrim, "game.bin"), new byte[100]);

            var viewModel = await LoadBackupRestoreAsync();
            await viewModel.InstalledGamesCheck;

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.VortexTargets.VisibleTargets, Is.Empty);
                Assert.That(viewModel.HasShrunkGames, Is.True);
            });

            await viewModel.AcceptCurrentSizesCommand.ExecuteAsync(null);

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.VortexTargets.VisibleTargets, Has.Count.EqualTo(1));
                Assert.That(viewModel.HasShrunkGames, Is.False);
                Assert.That(viewModel.HasHiddenGamesSummary, Is.False);
            });
        }

        [Test]
        public async Task Settings_ShowOnlyInstalledGames_IsSavedAndOffForOldSettingsFiles()
        {
            var store = new JsonSettingsStore();
            var settingsPath = _paths.GetPaths().SettingsFilePath;
            await store.SaveAsync(settingsPath, AppSettings.Default(Path.Combine(_root, "Backups")));

            var json = await File.ReadAllTextAsync(settingsPath);
            await File.WriteAllTextAsync(settingsPath, System.Text.RegularExpressions.Regex.Replace(json, "\\s*\"ShowOnlyInstalledGames\":\\s*false,?", string.Empty));

            var viewModel = new SettingsViewModel(store, _paths, _dialogs, new FakeThemeService(), _coordinator, _output);
            await viewModel.LoadCommand.ExecuteAsync(null);

            Assert.That(viewModel.ShowOnlyInstalledGames, Is.False);

            viewModel.ShowOnlyInstalledGames = true;
            await viewModel.SaveCommand.ExecuteAsync(null);

            Assert.That((await store.LoadAsync(settingsPath)).ShowOnlyInstalledGames, Is.True);
        }

        [Test]
        public async Task SelectAll_TogglesBetweenSelectAndDeselect()
        {
            var viewModel = await CreateBackupRestoreAsync("Skyrim", "Fallout 4", "Witcher 3");

            Assert.That(viewModel.VortexTargets.SelectAllButtonText, Is.EqualTo("Select all"));

            viewModel.VortexTargets.ToggleSelectAllCommand.Execute(null);

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.Targets.All(target => target.IsSelected), Is.True);
                Assert.That(viewModel.VortexTargets.SelectAllButtonText, Is.EqualTo("Deselect all"));
                Assert.That(viewModel.SelectedTargetsSummary, Is.EqualTo("3 of 3 selected"));
            });

            viewModel.VortexTargets.ToggleSelectAllCommand.Execute(null);

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.Targets.Any(target => target.IsSelected), Is.False);
                Assert.That(viewModel.VortexTargets.SelectAllButtonText, Is.EqualTo("Select all"));
            });
        }

        [Test]
        public async Task SelectAll_WithSearch_AffectsOnlyMatchingGames_AndClearResetsAll()
        {
            var viewModel = await CreateBackupRestoreAsync("Skyrim", "Skyrim VR", "Fallout 4");

            viewModel.TargetFilter = "sky";
            viewModel.VortexTargets.ToggleSelectAllCommand.Execute(null);

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.VortexTargets.VisibleTargets.Select(target => target.GameName), Is.EquivalentTo(new[] { "Skyrim", "Skyrim VR" }));
                Assert.That(viewModel.Targets.Where(target => target.IsSelected).Select(target => target.GameName), Is.EquivalentTo(new[] { "Skyrim", "Skyrim VR" }));
            });

            viewModel.TargetFilter = string.Empty;
            viewModel.ClearTargetSelectionCommand.Execute(null);

            Assert.That(viewModel.Targets.Any(target => target.IsSelected), Is.False);
        }

        [Test]
        public async Task Components_AreAllOffByDefault()
        {
            var viewModel = await CreateBackupRestoreAsync("Skyrim");

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.IncludeStaging, Is.False);
                Assert.That(viewModel.IncludeGameConfigs, Is.False);
                Assert.That(viewModel.IncludeGameChanges, Is.False);
                Assert.That(viewModel.IncludeVortexAppData, Is.False);
                Assert.That(viewModel.IncludeCustomFolders, Is.False);
            });
        }

        [Test]
        public async Task FinishedBackup_DeselectsGames_AndKeepsComponentToggles()
        {
            var viewModel = await CreateBackupRestoreAsync("Skyrim", "Fallout 4");
            viewModel.VortexTargets.ToggleSelectAllCommand.Execute(null);
            viewModel.IncludeStaging = true;
            viewModel.IncludeGameConfigs = true;
            viewModel.IncludeCustomFolders = true;

            await viewModel.StartBackupCommand.ExecuteAsync(null);

            Assert.Multiple(() =>
            {
                Assert.That(_runner.RunCount, Is.EqualTo(1));
                Assert.That(_runner.LastOptions, Is.EqualTo(new BackupOptions(true, true, false, true)));
                Assert.That(viewModel.Targets.Any(target => target.IsSelected), Is.False);
                Assert.That(viewModel.IncludeStaging, Is.True);
                Assert.That(viewModel.IncludeGameConfigs, Is.True);
                Assert.That(viewModel.IncludeCustomFolders, Is.True);
                Assert.That(viewModel.IncludeVortexAppData, Is.False);
            });
        }

        [Test]
        public async Task CancelledBackup_KeepsSelectionForRetry()
        {
            var viewModel = await CreateBackupRestoreAsync("Skyrim");
            viewModel.VortexTargets.ToggleSelectAllCommand.Execute(null);
            viewModel.IncludeGameConfigs = true;
            _runner.OnRun = (_, _) => throw new OperationCanceledException();

            await viewModel.StartBackupCommand.ExecuteAsync(null);

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.Targets.Single().IsSelected, Is.True);
                Assert.That(viewModel.IncludeGameConfigs, Is.True);
                Assert.That(_output.Entries.Any(entry => entry.Severity == OutputSeverity.Warning && entry.Message.Contains("cancelled")), Is.True);
            });
        }

        [Test]
        public async Task StartBackup_WhileAnotherOperationRuns_IsRejected()
        {
            var viewModel = await CreateBackupRestoreAsync("Skyrim");
            viewModel.VortexTargets.ToggleSelectAllCommand.Execute(null);
            viewModel.IncludeStaging = true;

            using (_coordinator.TryBegin("Purge"))
            {
                Assert.That(viewModel.CanStartOperation, Is.False);
                await viewModel.StartBackupCommand.ExecuteAsync(null);
            }

            Assert.Multiple(() =>
            {
                Assert.That(_runner.RunCount, Is.Zero);
                Assert.That(_output.Entries.Any(entry => entry.Severity == OutputSeverity.Error && entry.Message.Contains("Purge")), Is.True);
                Assert.That(viewModel.CanStartOperation, Is.True);
            });
        }

        [Test]
        public async Task StartBackup_WithoutSelection_ReportsErrorBeforeStarting()
        {
            var viewModel = await CreateBackupRestoreAsync("Skyrim");

            await viewModel.StartBackupCommand.ExecuteAsync(null);

            Assert.Multiple(() =>
            {
                Assert.That(_runner.RunCount, Is.Zero);
                Assert.That(_output.Entries.Select(entry => entry.Message), Has.None.Contains("started"));
                Assert.That(_output.Entries.Single().Severity, Is.EqualTo(OutputSeverity.Error));
            });
        }

        [Test]
        public async Task Settings_VortexAppDataFolder_IsLoadedValidatedAndSaved()
        {
            var store = new JsonSettingsStore();
            var settingsPath = _paths.GetPaths().SettingsFilePath;
            await store.SaveAsync(settingsPath, AppSettings.Default(Path.Combine(_root, "Backups")) with { VortexAppDataPath = @"D:\Vortex" });

            var viewModel = new SettingsViewModel(store, _paths, _dialogs, new FakeThemeService(), _coordinator, _output);
            await viewModel.LoadCommand.ExecuteAsync(null);

            Assert.That(viewModel.VortexAppDataPath, Is.EqualTo(@"D:\Vortex"));

            viewModel.VortexAppDataPath = "  ";
            await viewModel.SaveCommand.ExecuteAsync(null);

            var afterInvalidSave = await store.LoadAsync(settingsPath);

            Assert.Multiple(() =>
            {
                Assert.That(_output.Entries.Any(entry => entry.Severity == OutputSeverity.Error && entry.Message.Contains("Vortex AppData folder cannot be empty")), Is.True);
                Assert.That(afterInvalidSave.VortexAppDataPath, Is.EqualTo(@"D:\Vortex"));
            });

            viewModel.VortexAppDataPath = @" E:\Vortex ";
            await viewModel.SaveCommand.ExecuteAsync(null);

            Assert.That((await store.LoadAsync(settingsPath)).VortexAppDataPath, Is.EqualTo(@"E:\Vortex"));
        }

        [Test]
        public async Task Settings_CompressionSpeedFields_AreLoadedValidatedAndSaved()
        {
            var store = new JsonSettingsStore();
            var settingsPath = _paths.GetPaths().SettingsFilePath;
            await store.SaveAsync(settingsPath, AppSettings.Default(Path.Combine(_root, "Backups")));

            var viewModel = new SettingsViewModel(store, _paths, _dialogs, new FakeThemeService(), _coordinator, _output);
            await viewModel.LoadCommand.ExecuteAsync(null);

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.ParallelArchives, Is.EqualTo(Environment.ProcessorCount));
                Assert.That(viewModel.StoreComponentsLargerThanGigabytes, Is.Zero);
                Assert.That(viewModel.NoCompressionExtensionsText, Does.Contain(".ba2"));
                Assert.That(viewModel.ParallelArchivesDescription, Does.Contain(Environment.ProcessorCount.ToString()));
            });

            viewModel.ParallelArchives = 0;
            await viewModel.SaveCommand.ExecuteAsync(null);
            Assert.That(_output.Entries.Any(entry => entry.Severity == OutputSeverity.Error && entry.Message.Contains("Archives written at the same time")), Is.True);

            viewModel.ParallelArchives = 12;
            viewModel.StoreComponentsLargerThanGigabytes = 3;
            viewModel.NoCompressionExtensionsText = "BA2, *.pak\r\n.zip\r\n\r\n.ba2";
            await viewModel.SaveCommand.ExecuteAsync(null);
            var saved = await store.LoadAsync(settingsPath);

            Assert.Multiple(() =>
            {
                Assert.That(saved.ParallelArchives, Is.EqualTo(12));
                Assert.That(saved.StoreComponentsLargerThanBytes, Is.EqualTo(3L * 1024 * 1024 * 1024));
                Assert.That(saved.NoCompressionExtensions, Is.EqualTo(new[] { ".ba2", ".pak", ".zip" }));
            });
        }

        [Test]
        public async Task Backup_WithVortexAppData_AsksFirst_WhenVortexIsRunning()
        {
            var viewModel = await CreateBackupRestoreAsync("Skyrim");
            await new JsonSettingsStore().SaveAsync(_paths.GetPaths().SettingsFilePath, AppSettings.Default(Path.Combine(_root, "Backups")));
            await viewModel.LoadCommand.ExecuteAsync(null);
            viewModel.Targets.Single().IsSelected = true;
            viewModel.IncludeStaging = true;
            viewModel.IncludeVortexAppData = true;
            _vortex.IsRunning = true;
            _dialogs.ConfirmResult = false;

            await viewModel.StartBackupCommand.ExecuteAsync(null);
            Assert.That(_runner.RunCount, Is.Zero, "declined while Vortex runs");

            viewModel.IncludeVortexAppData = false;
            await viewModel.StartBackupCommand.ExecuteAsync(null);
            Assert.That(_runner.RunCount, Is.EqualTo(1), "staging alone does not need Vortex closed");
        }

        [Test]
        public async Task Dashboard_RejectsTheSharedVortexAppDataFolderNameAsGameName()
        {
            var viewModel = CreateDashboard();
            await viewModel.LoadCommand.ExecuteAsync(null);

            viewModel.AddProfileCommand.Execute(null);
            viewModel.EditGameName = "#vortex appdata#";
            viewModel.EditVortexStagingPath = @"D:\Staging";
            await viewModel.SaveProfileCommand.ExecuteAsync(null);

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.Profiles, Is.Empty);
                Assert.That(_output.Entries.Any(entry => entry.Severity == OutputSeverity.Error && entry.Message.Contains("is reserved")), Is.True);
            });
        }

        [Test]
        public async Task Settings_ThemeChange_IsAppliedAndSavedImmediately_ButNotOnLoad()
        {
            var theme = new FakeThemeService();
            var store = new JsonSettingsStore();
            var settingsPath = _paths.GetPaths().SettingsFilePath;
            await store.SaveAsync(settingsPath, AppSettings.Default(Path.Combine(_root, "Backups")) with { Theme = AppTheme.Light });

            var viewModel = new SettingsViewModel(store, _paths, _dialogs, theme, _coordinator, _output);
            await viewModel.LoadCommand.ExecuteAsync(null);

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.SelectedTheme, Is.EqualTo(AppTheme.Light));
                Assert.That(theme.Applied, Is.Empty, "loading must not re-apply or re-save the theme");
            });

            viewModel.SelectedTheme = AppTheme.Dark;

            var saved = AppTheme.Light;

            for (var attempt = 0; attempt < 50 && saved != AppTheme.Dark; attempt++)
            {
                await Task.Delay(20);
                saved = (await store.LoadAsync(settingsPath)).Theme;
            }

            Assert.Multiple(() =>
            {
                Assert.That(theme.Applied, Is.EqualTo(new[] { AppTheme.Dark }));
                Assert.That(saved, Is.EqualTo(AppTheme.Dark));
            });
        }

        private DashboardViewModel CreateDashboard() => new(
            new JsonProfileStore(),
            new PathDriveService(new FakeDrives()),
            new FakeDrives(),
            _paths,
            _dialogs,
            _dialogs,
            _coordinator,
            _output);

        [Test]
        public async Task Dashboard_CustomPaths_AddRemoveAndSave()
        {
            var folder = Directory.CreateDirectory(Path.Combine(_root, "AddOns")).FullName;
            var file = Path.Combine(_root, "UserSettings.txt");
            File.WriteAllText(file, "x");
            await new JsonProfileStore().SaveAsync(_paths.GetPaths().ProfilesFilePath,
                [new BackupProfile { GameName = "ESO", CustomFolders = [folder] }]);

            var viewModel = CreateDashboard();
            await viewModel.LoadCommand.ExecuteAsync(null);
            viewModel.EditProfileCommand.Execute(viewModel.Profiles.Single());

            Assert.That(viewModel.EditCustomPaths.Single().IsFile, Is.False);

            _dialogs.NextFiles.AddRange([file, file.ToUpperInvariant()]);
            viewModel.AddCustomFilesCommand.Execute(null);

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.EditCustomPaths, Has.Count.EqualTo(2), "the same file is added only once");
                Assert.That(viewModel.EditCustomPaths[1].IsFile, Is.True);
                Assert.That(viewModel.EditCustomPaths[1].KindLabel, Is.EqualTo("File"));
            });

            viewModel.RemoveCustomPathCommand.Execute(viewModel.EditCustomPaths[0]);
            await viewModel.SaveProfileCommand.ExecuteAsync(null);

            var saved = await new JsonProfileStore().LoadAsync(_paths.GetPaths().ProfilesFilePath);
            Assert.That(saved.Single().CustomFolders, Is.EqualTo(new[] { file }));
        }

        [Test]
        public async Task Dashboard_ProfileWithoutVortex_SavesNoVortexPaths_AndOldProfilesLoadAsVortex()
        {
            var game = Directory.CreateDirectory(Path.Combine(_root, "Anno 1404")).FullName;
            File.WriteAllText(_paths.GetPaths().ProfilesFilePath, """[ { "GameName": "Skyrim", "VortexStagingPath": "D:\\Staging" } ]""");

            var viewModel = CreateDashboard();
            await viewModel.LoadCommand.ExecuteAsync(null);

            Assert.That(viewModel.Profiles.Single().UsesVortex, Is.True, "profiles saved before the setting are Vortex profiles");

            viewModel.AddProfileCommand.Execute(null);
            viewModel.EditGameName = "Anno 1404";
            viewModel.EditVortexStagingPath = @"D:\Staging";
            viewModel.EditGameRootPath = game;
            viewModel.EditUsesVortex = false;
            await viewModel.SaveProfileCommand.ExecuteAsync(null);

            var anno = (await new JsonProfileStore().LoadAsync(_paths.GetPaths().ProfilesFilePath)).Single(profile => profile.GameName == "Anno 1404");

            Assert.Multiple(() =>
            {
                Assert.That(anno.UsesVortex, Is.False);
                Assert.That(anno.VortexStagingPath, Is.Null);
                Assert.That(anno.GameRootPath, Is.EqualTo(game));
                Assert.That(viewModel.Profiles.Single(item => item.GameName == "Anno 1404").HasVortexStagingPath, Is.False);
            });
        }

        [Test]
        public async Task Dashboard_ProfileWithoutVortex_NeedsGameFolderOrCustomPath()
        {
            var viewModel = CreateDashboard();
            await viewModel.LoadCommand.ExecuteAsync(null);

            viewModel.AddProfileCommand.Execute(null);
            viewModel.EditGameName = "ESO";
            viewModel.EditVortexStagingPath = @"D:\Staging";   // hidden once Vortex is off, so it does not count
            viewModel.EditUsesVortex = false;
            await viewModel.SaveProfileCommand.ExecuteAsync(null);

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.Profiles, Is.Empty);
                Assert.That(_output.Entries.Any(entry => entry.Severity == OutputSeverity.Error && entry.Message.Contains("game folder or a custom path")), Is.True);
            });
        }

        [Test]
        public async Task Backup_VortexComponents_HiddenAndNotUsed_WhenOnlyNonVortexGamesAreSelected()
        {
            var viewModel = await CreateBackupRestoreAsync("Skyrim");
            await new JsonProfileStore().SaveAsync(_paths.GetPaths().ProfilesFilePath,
            [
                new BackupProfile { GameName = "Skyrim", VortexStagingPath = Path.Combine(_root, "Skyrim") },
                new BackupProfile { GameName = "ESO", UsesVortex = false, CustomFolders = [Path.Combine(_root, "AddOns")] }
            ]);
            await viewModel.LoadCommand.ExecuteAsync(null);
            viewModel.IncludeStaging = true;
            viewModel.IncludeVortexAppData = true;
            viewModel.IncludeCustomFolders = true;

            Assert.That(viewModel.ShowVortexComponents, Is.True, "nothing selected yet");

            viewModel.Targets.Single(target => target.GameName == "ESO").IsSelected = true;
            Assert.That(viewModel.ShowVortexComponents, Is.False);

            await viewModel.StartBackupCommand.ExecuteAsync(null);
            Assert.That(_runner.LastOptions, Is.EqualTo(new BackupOptions(false, false, false, true)));

            viewModel.Targets.Single(target => target.GameName == "Skyrim").IsSelected = true;
            Assert.That(viewModel.ShowVortexComponents, Is.True, "a Vortex game is selected");
        }

        private async Task<BackupRestoreViewModel> CreateMixedBackupRestoreAsync()
        {
            var viewModel = await CreateBackupRestoreAsync("Skyrim");
            await new JsonProfileStore().SaveAsync(_paths.GetPaths().ProfilesFilePath,
            [
                new BackupProfile { GameName = "Skyrim", VortexStagingPath = Path.Combine(_root, "Skyrim") },
                new BackupProfile { GameName = "Fallout 4", VortexStagingPath = Path.Combine(_root, "Fallout 4") },
                new BackupProfile { GameName = "ESO", UsesVortex = false, CustomFolders = [Path.Combine(_root, "AddOns")] },
                new BackupProfile { GameName = "Anno 1404", UsesVortex = false, GameRootPath = Path.Combine(_root, "Anno") }
            ]);
            await viewModel.LoadCommand.ExecuteAsync(null);
            return viewModel;
        }

        [Test]
        public async Task Picker_SplitsVortexAndOtherGames_EachWithItsOwnSelectAll()
        {
            var viewModel = await CreateMixedBackupRestoreAsync();

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.VortexTargets.VisibleTargets.Select(target => target.GameName), Is.EqualTo(new[] { "Fallout 4", "Skyrim" }));
                Assert.That(viewModel.OtherTargets.VisibleTargets.Select(target => target.GameName), Is.EqualTo(new[] { "Anno 1404", "ESO" }));
                Assert.That(viewModel.VortexTargets.Summary, Is.EqualTo("0 of 2 selected"));
            });

            viewModel.VortexTargets.ToggleSelectAllCommand.Execute(null);

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.Targets.Where(target => target.IsSelected).Select(target => target.GameName), Is.EquivalentTo(new[] { "Skyrim", "Fallout 4" }));
                Assert.That(viewModel.VortexTargets.SelectAllButtonText, Is.EqualTo("Deselect all"));
                Assert.That(viewModel.OtherTargets.SelectAllButtonText, Is.EqualTo("Select all"));
                Assert.That(viewModel.OtherTargets.IsLocked, Is.True, "other games are locked while Vortex games are selected");
                Assert.That(viewModel.OtherTargets.ToggleSelectAllCommand.CanExecute(null), Is.False);
                Assert.That(viewModel.OtherTargets.Targets.All(target => !target.IsSelectable), Is.True);
                Assert.That(viewModel.OtherTargets.Summary, Does.Contain("clear the Vortex selection"));
                Assert.That(viewModel.VortexTargets.IsLocked, Is.False);
                Assert.That(viewModel.ShowVortexComponents, Is.True);
            });

            // Deselecting the group unlocks the other one; then only other games are picked.
            viewModel.VortexTargets.ToggleSelectAllCommand.Execute(null);
            Assert.That(viewModel.OtherTargets.IsLocked, Is.False);

            viewModel.OtherTargets.ToggleSelectAllCommand.Execute(null);

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.Targets.Where(target => target.IsSelected).Select(target => target.GameName), Is.EquivalentTo(new[] { "ESO", "Anno 1404" }));
                Assert.That(viewModel.VortexTargets.IsLocked, Is.True);
                Assert.That(viewModel.VortexTargets.Targets.All(target => !target.IsSelectable), Is.True);
                Assert.That(viewModel.ShowVortexComponents, Is.False);
            });

            viewModel.ClearTargetSelectionCommand.Execute(null);

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.VortexTargets.IsLocked, Is.False);
                Assert.That(viewModel.OtherTargets.IsLocked, Is.False);
                Assert.That(viewModel.Targets.All(target => target.IsSelectable), Is.True);
            });
        }

        [Test]
        public async Task Picker_SearchFiltersBothGroups_AndSelectAllOnlyTakesMatchingGamesOfItsGroup()
        {
            var viewModel = await CreateMixedBackupRestoreAsync();

            viewModel.TargetFilter = "o";
            viewModel.OtherTargets.ToggleSelectAllCommand.Execute(null);

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.VortexTargets.VisibleTargets.Select(target => target.GameName), Is.EqualTo(new[] { "Fallout 4" }));
                Assert.That(viewModel.OtherTargets.VisibleTargets.Select(target => target.GameName), Is.EqualTo(new[] { "Anno 1404", "ESO" }));
                Assert.That(viewModel.Targets.Where(target => target.IsSelected).Select(target => target.GameName), Is.EquivalentTo(new[] { "Anno 1404", "ESO" }));
            });

            viewModel.TargetFilter = "eso";

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.VortexTargets.IsVisible, Is.False, "a group without matches is hidden");
                Assert.That(viewModel.OtherTargets.IsVisible, Is.True);
                Assert.That(viewModel.HasNoVisibleTargets, Is.False);
            });

            viewModel.TargetFilter = "nothing matches";
            Assert.That(viewModel.HasNoVisibleTargets, Is.True);
        }

        [Test]
        public async Task Backup_RefusesAMixedSelection()
        {
            var viewModel = await CreateMixedBackupRestoreAsync();
            viewModel.IncludeGameConfigs = true;

            // The UI never allows this (the other group is locked), but a mixed selection must not reach the runner.
            viewModel.Targets.Single(target => target.GameName == "Skyrim").IsSelected = true;
            viewModel.Targets.Single(target => target.GameName == "ESO").IsSelected = true;

            await viewModel.StartBackupCommand.ExecuteAsync(null);

            Assert.Multiple(() =>
            {
                Assert.That(_runner.RunCount, Is.Zero);
                Assert.That(_output.Entries.Any(entry => entry.Severity == OutputSeverity.Error && entry.Message.Contains("separate runs")), Is.True);
            });
        }

        [Test]
        public void CustomPathItem_DetectsMissingPaths_AndSwapsDrive()
        {
            var item = new CustomPathItemViewModel(@"D:\Games\Missing\settings.ini", new PathDriveService(new FakeDrives()));

            Assert.Multiple(() =>
            {
                Assert.That(item.IsMissing, Is.True);
                Assert.That(item.KindLabel, Is.EqualTo("Not found"));
                Assert.That(item.Drive, Is.EqualTo("D:"));
            });

            item.Drive = "C:";

            Assert.That(item.Path, Is.EqualTo(@"C:\Games\Missing\settings.ini"));
        }
    }
}