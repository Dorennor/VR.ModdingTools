using UniversalBackupTool.Interface.Interfaces.Appearance;
using UniversalBackupTool.Interface.Interfaces.Backups;
using UniversalBackupTool.Interface.Interfaces.Dialogs;
using UniversalBackupTool.Interface.Interfaces.Paths;
using UniversalBackupTool.Interface.Interfaces.Profiles;
using UniversalBackupTool.Interface.Interfaces.Vortex;
using UniversalBackupTool.Model.Models.Backups;
using UniversalBackupTool.Model.Models.Jobs;
using UniversalBackupTool.Model.Models.Operations;
using UniversalBackupTool.Model.Models.Profiles;
using UniversalBackupTool.Model.Models.Settings;
using UniversalBackupTool.Service.Services.Operations;
using UniversalBackupTool.Service.Services.Paths;
using UniversalBackupTool.Service.Services.Profiles;
using UniversalBackupTool.Service.Services.Settings;
using UniversalBackupTool.Service.Services.Snapshots;
using UniversalBackupTool.ViewModel.ViewModels.Backup;
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

        public IReadOnlyList<ExistingFullGameArchive> ExistingFullGameArchives { get; set; } = [];

        public Action<IReadOnlyList<BackupProfile>>? OnFindArchives { get; set; }

        public IReadOnlyList<ExistingFullGameArchive> FindExistingFullGameArchives(IReadOnlyList<BackupProfile> profiles)
        {
            OnFindArchives?.Invoke(profiles);
            return ExistingFullGameArchives;
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

        public string? LastConfirmMessage { get; private set; }

        public bool Confirm(string title, string message)
        {
            LastConfirmMessage = message;
            return ConfirmResult;
        }
    }

    internal sealed class FakeThemeService : IThemeService
    {
        public List<AppTheme> Applied { get; } = [];

        public void Apply(AppTheme theme) => Applied.Add(theme);
    }

    /// <summary>
    /// Every game has everything, for tests that are not about installed games.
    /// </summary>
    internal sealed class FakeGamePresence : IGamePresenceService
    {
        public GamePresence CheckFoldersExist(BackupProfile profile) => GamePresence.Unknown(profile.Id);

        public Task<IReadOnlyList<GamePresence>> CheckAsync(IReadOnlyCollection<BackupProfile> profiles) =>
            Task.FromResult<IReadOnlyList<GamePresence>>(profiles.Select(profile => GamePresence.Unknown(profile.Id)).ToList());
    }

    /// <summary>
    /// A leftover folder: it exists, but the full check, which takes a while, says the game is gone.
    /// </summary>
    internal sealed class SlowLeftoverPresence(Task gate) : IGamePresenceService
    {
        public GamePresence CheckFoldersExist(BackupProfile profile) => new(profile.Id, true, true, false, false);

        public async Task<IReadOnlyList<GamePresence>> CheckAsync(IReadOnlyCollection<BackupProfile> profiles)
        {
            await gate;

            return profiles.Select(profile => new GamePresence(profile.Id, false, false, false, false)).ToList();
        }
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
        private IGamePresenceService _presence = null!;

        private const int RealGameBytes = 11 * 1024 * 1024;

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
            _presence = new FakeGamePresence();
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

        /// <summary>
        /// Vortex games with an export folder, so the Full game switch is not locked.
        /// </summary>
        private async Task<BackupViewModel> CreateFullGameBackupAsync(params string[] games)
        {
            var profiles = games.Select(name => new BackupProfile
            {
                GameName = name,
                VortexStagingPath = Path.Combine(_root, name),
                UseFullGameExportPath = true,
                FullGameExportPath = Path.Combine(_root, "Export")
            }).ToList();
            await new JsonProfileStore().SaveAsync(_paths.GetPaths().ProfilesFilePath, profiles);

            return await LoadBackupAsync();
        }

        private async Task<BackupViewModel> CreateBackupAsync(params string[] games)
        {
            await SaveProfilesAsync(games);

            return await LoadBackupAsync();
        }

        private async Task<BackupViewModel> LoadBackupAsync()
        {
            var viewModel = new BackupViewModel(
                new JsonProfileStore(),
                new JsonSettingsStore(),
                _paths,
                _runner,
                new FakeJobStateService(),
                _dialogs,
                _vortex,
                _presence,
                _coordinator,
                _output);

            await viewModel.LoadCommand.ExecuteAsync(null);
            return viewModel;
        }

        private string CreateGameFolder(string name, int sizeBytes)
        {
            var folder = Path.Combine(_root, name);
            Directory.CreateDirectory(folder);
            using var stream = File.Create(Path.Combine(folder, "game.bin"));
            stream.SetLength(sizeBytes);

            return folder;
        }

        private void UseRealPresenceCheck() => _presence = new GamePresenceService(new JsonSnapshotStore(), _paths);

        private BackupProfile InstalledGame(string name, string? exportFolder = null) => new()
        {
            GameName = name,
            GameRootPath = CreateGameFolder(name, RealGameBytes),
            UseFullGameExportPath = exportFolder is not null,
            FullGameExportPath = exportFolder
        };

        private BackupProfile UninstalledGame(string name) => new()
        {
            GameName = name,
            GameRootPath = Path.Combine(_root, "Uninstalled", name)
        };

        private BackupProfile ModsOnlyGame(string name) => UninstalledGame(name) with { VortexStagingPath = Path.GetDirectoryName(CreateGameFolder(Path.Combine("Staging", name, "Mod"), 10)) };

        private async Task<BackupViewModel> LoadCheckedBackupAsync(params BackupProfile[] profiles)
        {
            UseRealPresenceCheck();
            await new JsonProfileStore().SaveAsync(_paths.GetPaths().ProfilesFilePath, profiles);

            var viewModel = await LoadBackupAsync();
            await viewModel.InstalledGamesCheck;

            return viewModel;
        }

        private static BackupTargetItemViewModel Target(BackupViewModel viewModel, string name) =>
            viewModel.Targets.Single(target => target.GameName == name);

        [Test]
        public async Task HideUninstalled_IsOnByDefault_AndHidesGamesWithNothingToBackUp()
        {
            var viewModel = await LoadCheckedBackupAsync(InstalledGame("Skyrim"), UninstalledGame("Fallout 4"));

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.HideUninstalledGames, Is.True);
                Assert.That(viewModel.VortexTargets.VisibleTargets.Select(target => target.GameName), Is.EqualTo(new[] { "Skyrim" }));
                Assert.That(viewModel.VortexTargets.Summary, Is.EqualTo("0 of 1 selected"));
                Assert.That(viewModel.HiddenGamesSummary, Does.Contain("Fallout 4"));
            });
        }

        [Test]
        public async Task HideUninstalled_NoGameInstalled_ExplainsTheEmptyList()
        {
            var viewModel = await LoadCheckedBackupAsync(UninstalledGame("Skyrim"));

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.HasNoVisibleTargets, Is.True);
                Assert.That(viewModel.NoVisibleTargetsText, Does.Contain("Hide uninstalled games"));
            });
        }

        [Test]
        public async Task HideUninstalled_Off_ListsAndLabelsThem_AndTheChoiceIsSaved()
        {
            var viewModel = await LoadCheckedBackupAsync(InstalledGame("Skyrim"), UninstalledGame("Fallout 4"));

            viewModel.HideUninstalledGames = false;
            await viewModel.HideSettingSave;

            var settings = await new JsonSettingsStore().LoadAsync(_paths.GetPaths().SettingsFilePath);
            var reloaded = await LoadBackupAsync();
            await reloaded.InstalledGamesCheck;

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.VortexTargets.VisibleTargets, Has.Count.EqualTo(2));
                Assert.That(viewModel.HasHiddenGamesSummary, Is.False);
                Assert.That(Target(viewModel, "Fallout 4").DisplayName, Is.EqualTo("Fallout 4 (not installed)"));
                Assert.That(Target(viewModel, "Fallout 4").IsDimmed, Is.True);
                Assert.That(Target(viewModel, "Skyrim").DisplayName, Is.EqualTo("Skyrim"));
                Assert.That(settings.ShowOnlyInstalledGames, Is.False);
                Assert.That(reloaded.HideUninstalledGames, Is.False);
                Assert.That(reloaded.VortexTargets.VisibleTargets, Has.Count.EqualTo(2));
            });
        }

        [Test]
        public async Task HideUninstalled_SavingKeepsTheOtherSettings()
        {
            var store = new JsonSettingsStore();
            var settingsPath = _paths.GetPaths().SettingsFilePath;
            await store.SaveAsync(settingsPath, AppSettings.Default(Path.Combine(_root, "Backups")) with { MaxPreservedBackupsPerGame = 7 });

            var viewModel = await LoadCheckedBackupAsync(InstalledGame("Skyrim"));

            // Changed on the Settings page after the Backup page was loaded.
            await store.SaveAsync(settingsPath, await store.LoadAsync(settingsPath) with { MaxPreservedBackupsPerGame = 9 });

            viewModel.HideUninstalledGames = false;
            await viewModel.HideSettingSave;

            var saved = await store.LoadAsync(settingsPath);

            Assert.Multiple(() =>
            {
                Assert.That(saved.ShowOnlyInstalledGames, Is.False);
                Assert.That(saved.MaxPreservedBackupsPerGame, Is.EqualTo(9));
            });
        }

        [Test]
        public async Task HidingAGame_DeselectsIt()
        {
            var viewModel = await LoadCheckedBackupAsync(InstalledGame("Skyrim"), UninstalledGame("Fallout 4"));
            viewModel.HideUninstalledGames = false;
            Target(viewModel, "Fallout 4").IsSelected = true;

            viewModel.HideUninstalledGames = true;
            await viewModel.HideSettingSave;

            Assert.That(Target(viewModel, "Fallout 4").IsSelected, Is.False);
        }

        [Test]
        public async Task ModsOnlyGame_StaysListed_WithItsGameComponentsLocked()
        {
            var viewModel = await LoadCheckedBackupAsync(ModsOnlyGame("Skyrim"));
            viewModel.IncludeStaging = true;
            viewModel.IncludeGameConfigs = true;
            viewModel.IncludeFullGame = true;

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.CanUseGameFolder, Is.True, "nothing is locked without a selection");
                Assert.That(viewModel.IsGameConfigsOn, Is.True);
                Assert.That(viewModel.HasLockedComponents, Is.False);
            });

            Target(viewModel, "Skyrim").IsSelected = true;

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.VortexTargets.VisibleTargets.Single().DisplayName, Is.EqualTo("Skyrim (mods only)"));
                Assert.That(Target(viewModel, "Skyrim").IsDimmed, Is.False);
                Assert.That(viewModel.CanUseStaging, Is.True);
                Assert.That(viewModel.IsStagingOn, Is.True);
                Assert.That(viewModel.CanUseGameFolder, Is.False);
                Assert.That(viewModel.IsGameConfigsOn, Is.False, "a locked switch reads Off");
                Assert.That(viewModel.CanUseFullGame, Is.False);
                Assert.That(viewModel.IsFullGameOn, Is.False);
                Assert.That(viewModel.LockedComponentsSummary, Does.Contain("no selected game is installed"));
            });

            await viewModel.StartBackupCommand.ExecuteAsync(null);

            Assert.Multiple(() =>
            {
                Assert.That(_runner.LastOptions!.IncludeStaging, Is.True);
                Assert.That(_runner.LastOptions.IncludeGameConfigs, Is.False);
                Assert.That(_runner.LastOptions.IncludeFullGame, Is.False);
            });
        }

        [Test]
        public async Task LockedSwitch_KeepsTheUsersChoice_ForTheNextSelection()
        {
            var viewModel = await LoadCheckedBackupAsync(ModsOnlyGame("Skyrim"), InstalledGame("Fallout 4"));
            viewModel.IncludeGameConfigs = true;

            Target(viewModel, "Skyrim").IsSelected = true;
            Assert.That(viewModel.IsGameConfigsOn, Is.False);

            Target(viewModel, "Skyrim").IsSelected = false;
            Target(viewModel, "Fallout 4").IsSelected = true;
            Assert.That(viewModel.IsGameConfigsOn, Is.True);
        }

        [Test]
        public async Task Locks_FollowWhatTheSelectedGamesHave()
        {
            var viewModel = await LoadCheckedBackupAsync(InstalledGame("No Export"), InstalledGame("With Export", Path.Combine(_root, "Export")));

            Target(viewModel, "No Export").IsSelected = true;

            Assert.Multiple(() =>
            {
                Assert.That(viewModel.CanUseGameFolder, Is.True);
                Assert.That(viewModel.CanUseFullGame, Is.False);
                Assert.That(viewModel.CanUseStaging, Is.False);
                Assert.That(viewModel.CanUseCustomPaths, Is.False);
                Assert.That(viewModel.LockedComponentsSummary, Does.Contain("Full game is locked"));
            });

            Target(viewModel, "With Export").IsSelected = true;

            Assert.That(viewModel.CanUseFullGame, Is.True, "stays available when one selected game can use it");
        }

        [Test]
        public async Task MixedSelection_TellsTheRunnerWhichGamesAreRestricted_AndAsksOnlyAboutInstalledArchives()
        {
            var export = Path.Combine(_root, "Export");
            var installed = InstalledGame("Fallout 4", export);
            var modsOnly = ModsOnlyGame("Skyrim") with { UseFullGameExportPath = true, FullGameExportPath = export };
            IReadOnlyList<BackupProfile> asked = [];
            _runner.OnFindArchives = profiles => asked = profiles;

            var viewModel = await LoadCheckedBackupAsync(installed, modsOnly);
            viewModel.IncludeFullGame = true;
            Target(viewModel, "Fallout 4").IsSelected = true;
            Target(viewModel, "Skyrim").IsSelected = true;

            await viewModel.StartBackupCommand.ExecuteAsync(null);

            Assert.Multiple(() =>
            {
                Assert.That(_runner.LastOptions!.IncludeFullGame, Is.True);
                Assert.That(_runner.LastOptions.RestrictedGames![modsOnly.Id].HasGameFolder, Is.False);
                Assert.That(asked.Select(profile => profile.GameName), Is.EqualTo(new[] { "Fallout 4" }));
            });
        }

        [Test]
        public async Task Backup_WaitsForTheFullCheck_BeforeDecidingWhatIsLocked()
        {
            var gate = new TaskCompletionSource();
            var profile = UninstalledGame("Skyrim");

            _presence = new SlowLeftoverPresence(gate.Task);
            await new JsonProfileStore().SaveAsync(_paths.GetPaths().ProfilesFilePath, [profile]);

            var viewModel = await LoadBackupAsync();
            viewModel.HideUninstalledGames = false;
            await viewModel.HideSettingSave;
            viewModel.IncludeGameConfigs = true;
            viewModel.IncludeVortexAppData = true;
            Target(viewModel, "Skyrim").IsSelected = true;

            var backup = viewModel.StartBackupCommand.ExecuteAsync(null);
            Assert.That(_runner.RunCount, Is.Zero);

            gate.SetResult();
            await backup;

            // The folder exists, so the first answer allowed Game configs; the full check found only leftovers.
            Assert.Multiple(() =>
            {
                Assert.That(_runner.RunCount, Is.EqualTo(1));
                Assert.That(_runner.LastOptions!.IncludeGameConfigs, Is.False);
                Assert.That(_runner.LastOptions.IncludeVortexAppData, Is.True);
            });
        }

        [TestCase(4, null, true, TestName = "OldSettings_AreSwitchedOnOnce")]
        [TestCase(5, false, false, TestName = "CurrentSettings_KeepTheUsersChoice")]
        public async Task HideUninstalled_Migration(int version, bool? stored, bool expected)
        {
            var store = new JsonSettingsStore();
            var path = _paths.GetPaths().SettingsFilePath;
            var field = stored is null ? string.Empty : $"\"ShowOnlyInstalledGames\": {stored.Value.ToString().ToLowerInvariant()}, ";
            await File.WriteAllTextAsync(path, $$"""{ "GoogleDriveBackupPath": "D:\\Backups", "VortexAppDataPath": "C:\\Vortex", {{field}}"DefaultsVersion": {{version}} }""");

            Assert.That((await store.LoadAsync(path)).ShowOnlyInstalledGames, Is.EqualTo(expected));
            Assert.That((await store.LoadAsync(path)).ShowOnlyInstalledGames, Is.EqualTo(expected), "the upgrade is saved");
        }

        [Test]
        public async Task SelectAll_TogglesBetweenSelectAndDeselect()
        {
            var viewModel = await CreateBackupAsync("Skyrim", "Fallout 4", "Witcher 3");

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
            var viewModel = await CreateBackupAsync("Skyrim", "Skyrim VR", "Fallout 4");

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
            var viewModel = await CreateBackupAsync("Skyrim");

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
            var viewModel = await CreateBackupAsync("Skyrim", "Fallout 4");
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
            var viewModel = await CreateBackupAsync("Skyrim");
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
            var viewModel = await CreateBackupAsync("Skyrim");
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
            var viewModel = await CreateBackupAsync("Skyrim");

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
            var viewModel = await CreateBackupAsync("Skyrim");
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
        public async Task FullGame_ExistingArchive_IsConfirmedOnceWithItsPathAndSize()
        {
            var viewModel = await CreateFullGameBackupAsync("Skyrim");
            viewModel.VortexTargets.ToggleSelectAllCommand.Execute(null);
            viewModel.IncludeFullGame = true;
            _runner.ExistingFullGameArchives = [new ExistingFullGameArchive("Skyrim", @"G:\Drive\Skyrim.zip", 3L * 1024 * 1024 * 1024)];
            _dialogs.ConfirmResult = false;

            await viewModel.StartBackupCommand.ExecuteAsync(null);

            Assert.Multiple(() =>
            {
                Assert.That(_runner.RunCount, Is.Zero, "declined: nothing runs and nothing is deleted");
                Assert.That(_dialogs.LastConfirmMessage, Does.Contain(@"G:\Drive\Skyrim.zip").And.Contain($"{3.0:0.0} GB"));
            });

            _dialogs.ConfirmResult = true;
            await viewModel.StartBackupCommand.ExecuteAsync(null);

            Assert.Multiple(() =>
            {
                Assert.That(_runner.RunCount, Is.EqualTo(1));
                Assert.That(_runner.LastOptions!.IncludeFullGame, Is.True);
                Assert.That(viewModel.IncludeFullGame, Is.True, "the toggle stays on after a backup");
            });
        }

        [Test]
        public async Task FullGame_WithoutExistingArchive_AsksNothing()
        {
            var viewModel = await CreateFullGameBackupAsync("Skyrim");
            viewModel.VortexTargets.ToggleSelectAllCommand.Execute(null);
            viewModel.IncludeFullGame = true;

            await viewModel.StartBackupCommand.ExecuteAsync(null);

            Assert.Multiple(() =>
            {
                Assert.That(_dialogs.LastConfirmMessage, Is.Null);
                Assert.That(_runner.RunCount, Is.EqualTo(1));
            });
        }

        [Test]
        public async Task FullGame_DoesNotNeedTheBackupDestination_OtherComponentsDo()
        {
            await new JsonSettingsStore().SaveAsync(_paths.GetPaths().SettingsFilePath, AppSettings.Default(string.Empty));

            var viewModel = await CreateFullGameBackupAsync("Skyrim");
            viewModel.VortexTargets.ToggleSelectAllCommand.Execute(null);
            viewModel.IncludeStaging = true;

            await viewModel.StartBackupCommand.ExecuteAsync(null);
            Assert.That(_runner.RunCount, Is.Zero, "staging needs the destination");

            viewModel.IncludeStaging = false;
            viewModel.IncludeFullGame = true;

            await viewModel.StartBackupCommand.ExecuteAsync(null);
            Assert.That(_runner.RunCount, Is.EqualTo(1));
        }

        [Test]
        public async Task Dashboard_FullGameExportFolder_IsValidatedSavedAndKeptWhileOff()
        {
            var viewModel = CreateDashboard();
            await viewModel.LoadCommand.ExecuteAsync(null);

            viewModel.AddProfileCommand.Execute(null);
            viewModel.EditGameName = "Skyrim";
            viewModel.EditGameRootPath = @"D:\Games\Skyrim";
            viewModel.EditUseFullGameExportPath = true;
            await viewModel.SaveProfileCommand.ExecuteAsync(null);

            Assert.That(viewModel.Profiles, Is.Empty, "the checkbox is on but no folder is chosen");

            viewModel.EditFullGameExportPath = @"G:\Drive\Games";
            await viewModel.SaveProfileCommand.ExecuteAsync(null);

            var saved = viewModel.Profiles.Single();

            Assert.Multiple(() =>
            {
                Assert.That(saved.FullGameExportFolder, Is.EqualTo(@"G:\Drive\Games"));
                Assert.That(saved.HasFullGameExportFolder, Is.True);
            });

            viewModel.EditProfileCommand.Execute(saved);
            Assert.That(viewModel.EditUseFullGameExportPath, Is.True);

            viewModel.EditUseFullGameExportPath = false;
            await viewModel.SaveProfileCommand.ExecuteAsync(null);

            var profile = (await new JsonProfileStore().LoadAsync(_paths.GetPaths().ProfilesFilePath)).Single();

            Assert.Multiple(() =>
            {
                Assert.That(profile.GetFullGameExportFolder(), Is.Null);
                Assert.That(profile.FullGameExportPath, Is.EqualTo(@"G:\Drive\Games"), "turning it back on does not ask again");
                Assert.That(viewModel.Profiles.Single().HasFullGameExportFolder, Is.False);
            });
        }

        [Test]
        public async Task Dashboard_FullGameExportFolder_NeedsTheGameFolder()
        {
            var viewModel = CreateDashboard();
            await viewModel.LoadCommand.ExecuteAsync(null);

            viewModel.AddProfileCommand.Execute(null);
            viewModel.EditGameName = "Skyrim";
            viewModel.EditVortexStagingPath = @"D:\Staging";
            viewModel.EditUseFullGameExportPath = true;
            viewModel.EditFullGameExportPath = @"G:\Drive";
            await viewModel.SaveProfileCommand.ExecuteAsync(null);

            Assert.That(viewModel.Profiles, Is.Empty);
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
            var viewModel = await CreateBackupAsync("Skyrim");
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

        private async Task<BackupViewModel> CreateMixedBackupAsync()
        {
            var viewModel = await CreateBackupAsync("Skyrim");
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
            var viewModel = await CreateMixedBackupAsync();

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
            var viewModel = await CreateMixedBackupAsync();

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
            var viewModel = await CreateMixedBackupAsync();
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