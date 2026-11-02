using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using UniversalBackupTool.Interface.Interfaces.Backups;
using UniversalBackupTool.Interface.Interfaces.Dialogs;
using UniversalBackupTool.Interface.Interfaces.Operations;
using UniversalBackupTool.Interface.Interfaces.Paths;
using UniversalBackupTool.Interface.Interfaces.Profiles;
using UniversalBackupTool.Interface.Interfaces.Settings;
using UniversalBackupTool.Interface.Interfaces.Vortex;
using UniversalBackupTool.Interfaces.Storage;
using UniversalBackupTool.Model.Models.Backups;
using UniversalBackupTool.Model.Models.Profiles;
using UniversalBackupTool.Model.Models.Settings;
using UniversalBackupTool.ViewModel.ViewModels.Base;

namespace UniversalBackupTool.ViewModel.ViewModels.Restore
{
    public sealed partial class RestoreViewModel : BasicViewModel
    {
        private static readonly BackupComponent[] ComponentOrder =
        [
            BackupComponent.StagingFolders,
            BackupComponent.GameConfigs,
            BackupComponent.GameAdditions,
            BackupComponent.GameChanges,
            BackupComponent.VortexAppData,
            BackupComponent.CustomFolders
        ];

        private readonly IProfileStore _profileStore;
        private readonly ISettingsStore _settingsStore;
        private readonly IAppStoragePathsProvider _storagePathsProvider;
        private readonly IRestoreService _restoreService;
        private readonly IFolderPickerService _folderPickerService;
        private readonly IConfirmationService _confirmationService;
        private readonly IPathDriveService _pathDriveService;
        private readonly IVortexProcessService _vortexProcessService;

        private AppSettings? _settings;
        private List<BackupProfile> _allProfiles = [];
        private BackupManifest? _restoreManifest;
        private int _backupListVersion;

        public RestoreViewModel(
            IProfileStore profileStore,
            ISettingsStore settingsStore,
            IAppStoragePathsProvider storagePathsProvider,
            IRestoreService restoreService,
            IFolderPickerService folderPickerService,
            IConfirmationService confirmationService,
            IPathDriveService pathDriveService,
            IVortexProcessService vortexProcessService,
            IOperationCoordinator operationCoordinator,
            IOperationOutputService operationOutputService)
            : base(operationOutputService, operationCoordinator)
        {
            _profileStore = profileStore ?? throw new ArgumentNullException(nameof(profileStore));
            _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
            _storagePathsProvider = storagePathsProvider ?? throw new ArgumentNullException(nameof(storagePathsProvider));
            _restoreService = restoreService ?? throw new ArgumentNullException(nameof(restoreService));
            _folderPickerService = folderPickerService ?? throw new ArgumentNullException(nameof(folderPickerService));
            _confirmationService = confirmationService ?? throw new ArgumentNullException(nameof(confirmationService));
            _pathDriveService = pathDriveService ?? throw new ArgumentNullException(nameof(pathDriveService));
            _vortexProcessService = vortexProcessService ?? throw new ArgumentNullException(nameof(vortexProcessService));

            PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(RestoreStagingDrive)
                    or nameof(RestoreGameConfigsDrive)
                    or nameof(RestoreAppDataDrive)
                    or nameof(RestoreCustomFoldersDrive))
                {
                    RefreshRestorePlan();
                }
            };
        }

        /// <summary>
        /// Every profile, whether or not the game is on this PC (a restore is how it comes back), and the shared Vortex
        /// AppData backups while any game uses Vortex.
        /// </summary>
        public ObservableCollection<RestoreGameOption> Games { get; } = [];

        [ObservableProperty]
        private RestoreGameOption? _selectedGame;

        partial void OnSelectedGameChanged(RestoreGameOption? value)
        {
            ClearChosenBackup();
            BackupGroups.Clear();
            BackupListLoad = RefreshBackupListAsync();
        }

        public ObservableCollection<RestoreBackupGroup> BackupGroups { get; } = [];

        /// <summary>
        /// Why the list has no rows; empty while it has some.
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasBackupListMessage))]
        private string _backupListMessage = string.Empty;

        public bool HasBackupListMessage => BackupListMessage.Length > 0;

        /// <summary>
        /// The running or last search for backups, which does not keep the page busy; lets a caller wait for its result.
        /// </summary>
        public Task BackupListLoad { get; private set; } = Task.CompletedTask;

        public ObservableCollection<string> RestorePlanLines { get; } = [];

        public IReadOnlyList<string> AvailableDrives { get; } = DriveInfo.GetDrives()
            .Where(drive => drive.IsReady)
            .Select(drive => drive.Name[..2])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(drive => drive, StringComparer.OrdinalIgnoreCase)
            .ToList();

        public bool HasRestoreManifest => RestoreSummary is not null;

        [ObservableProperty]
        private string? _restoreFolderPath;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasRestoreManifest))]
        private string? _restoreSummary;

        [ObservableProperty]
        private bool _restoreHasStaging;

        [ObservableProperty]
        private bool _restoreHasGameConfigs;

        [ObservableProperty]
        private bool _restoreHasGameAdditions;

        [ObservableProperty]
        private bool _restoreHasGameChanges;

        [ObservableProperty]
        private bool _restoreHasAppData;

        [ObservableProperty]
        private bool _restoreHasCustomFolders;

        [ObservableProperty]
        private bool _restoreStaging = true;

        [ObservableProperty]
        private bool _restoreGameConfigs = true;

        [ObservableProperty]
        private bool _restoreGameAdditions = true;

        [ObservableProperty]
        private bool _restoreGameChanges = true;

        [ObservableProperty]
        private bool _restoreAppData = true;

        [ObservableProperty]
        private bool _restoreCustomFolders = true;

        private string? _restoreStagingDrive;

        public string? RestoreStagingDrive
        {
            get => _restoreStagingDrive;
            set => SetProperty(ref _restoreStagingDrive, value);
        }

        private string? _restoreGameConfigsDrive;

        /// <summary>
        /// Drive of the game folder, shared by both game folder components (Game configs, New game files and Changed game files).
        /// </summary>
        public string? RestoreGameConfigsDrive
        {
            get => _restoreGameConfigsDrive;
            set => SetProperty(ref _restoreGameConfigsDrive, value);
        }

        private string? _restoreAppDataDrive;

        public string? RestoreAppDataDrive
        {
            get => _restoreAppDataDrive;
            set => SetProperty(ref _restoreAppDataDrive, value);
        }

        private string? _restoreCustomFoldersDrive;

        public string? RestoreCustomFoldersDrive
        {
            get => _restoreCustomFoldersDrive;
            set => SetProperty(ref _restoreCustomFoldersDrive, value);
        }

        partial void OnRestoreStagingChanged(bool value) => RefreshRestorePlan();

        partial void OnRestoreGameConfigsChanged(bool value) => RefreshRestorePlan();

        partial void OnRestoreGameAdditionsChanged(bool value) => RefreshRestorePlan();

        partial void OnRestoreGameChangesChanged(bool value) => RefreshRestorePlan();

        partial void OnRestoreAppDataChanged(bool value) => RefreshRestorePlan();

        partial void OnRestoreCustomFoldersChanged(bool value) => RefreshRestorePlan();

        [RelayCommand]
        private Task LoadAsync() => RunBusyAsync(async () =>
                {
                    var paths = _storagePathsProvider.GetPaths();

                    _settings = await _settingsStore.LoadAsync(paths.SettingsFilePath);
                    _allProfiles = (await _profileStore.LoadAsync(paths.ProfilesFilePath)).ToList();

                    UpdateGames();

                    if (RestoreFolderPath is not null && !Directory.Exists(RestoreFolderPath))
                        ClearChosenBackup();

                    RefreshRestorePlan();

                    BackupListLoad = RefreshBackupListAsync();
                });

        /// <summary>
        /// Rebuilds the game picker only when its entries changed, so returning to the page keeps the chosen game and backup.
        /// </summary>
        private void UpdateGames()
        {
            var games = _allProfiles
                .OrderBy(profile => profile.GameName, StringComparer.CurrentCultureIgnoreCase)
                .Select(profile => new RestoreGameOption(profile.GameName, profile.GameName))
                .Distinct()
                .ToList();

            if (_allProfiles.Any(profile => profile.UsesVortex))
                games.Add(new RestoreGameOption($"{VortexAppDataBackup.DisplayName} (all games)", VortexAppDataBackup.FolderName));

            if (Games.SequenceEqual(games))
                return;

            var previous = SelectedGame;

            Games.Clear();

            foreach (var game in games)
                Games.Add(game);

            SelectedGame = games.FirstOrDefault(game => game == previous);
        }

        private async Task RefreshBackupListAsync()
        {
            var version = ++_backupListVersion;
            var game = SelectedGame;
            var backupRoot = _settings?.GoogleDriveBackupPath;

            if (game is null)
            {
                BackupGroups.Clear();
                BackupListMessage = Games.Count == 0 ? "No profiles yet. Add one on the Profiles page." : "Choose a game to see its backups.";
                return;
            }

            if (string.IsNullOrWhiteSpace(backupRoot))
            {
                BackupGroups.Clear();
                BackupListMessage = "The backup destination folder is not configured. Set it on the Settings page.";
                return;
            }

            if (BackupGroups.Count == 0)
                BackupListMessage = $"Looking for backups of {game.DisplayName}...";

            try
            {
                var backups = await _restoreService.FindBackupsAsync(backupRoot, game.GameName);

                // Another game was chosen or the page was opened again meanwhile; the newer search decides.
                if (version != _backupListVersion)
                    return;

                BackupGroups.Clear();

                foreach (var group in GroupByComponent(backups))
                    BackupGroups.Add(group);

                BackupListMessage = backups.Count == 0 ? $"No backups of {game.DisplayName} found in {backupRoot}." : string.Empty;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (version != _backupListVersion)
                    return;

                BackupGroups.Clear();
                BackupListMessage = $"The backups of {game.DisplayName} could not be read: {exception.Message}";
            }
        }

        /// <summary>
        /// Every backup holds one component, except older ones made as a single archive, which come last.
        /// </summary>
        private static List<RestoreBackupGroup> GroupByComponent(IReadOnlyList<LocatedBackupManifest> backups)
        {
            return backups
                .GroupBy(backup => GetOnlyComponent(backup.Manifest))
                .OrderBy(group => group.Key is { } component ? Array.IndexOf(ComponentOrder, component) : ComponentOrder.Length)
                .Select(group => new RestoreBackupGroup(
                    group.Key is { } component ? GetComponentLabel(component) : "Several components",
                    group.Select(backup => new RestoreBackupItem(backup.Directory, backup.Manifest)).ToList()))
                .ToList();
        }

        private static BackupComponent? GetOnlyComponent(BackupManifest manifest)
        {
            var components = manifest.Units.Select(unit => unit.Component).Distinct().ToList();

            return components.Count == 1 ? components[0] : null;
        }

        private static string GetComponentLabel(BackupComponent component)
        {
            return component switch
            {
                BackupComponent.StagingFolders => "Staging folders",
                BackupComponent.GameConfigs => "Game configs",
                BackupComponent.GameAdditions => "New game files",
                BackupComponent.GameChanges => "Changed game files",
                BackupComponent.VortexAppData => "Vortex AppData",
                _ => "Custom paths"
            };
        }

        [RelayCommand]
        private void SelectBackup(RestoreBackupItem backup) => ShowRestoreOptions(backup.Folder, backup.Manifest);

        [RelayCommand]
        private Task BrowseRestoreFolderAsync() => RunBusyAsync(async () =>
                {
                    var selectedPath = _folderPickerService.PickFolder(RestoreFolderPath ?? _settings?.GoogleDriveBackupPath);

                    if (string.IsNullOrWhiteSpace(selectedPath))
                        return;

                    var manifest = await _restoreService.ReadManifestAsync(selectedPath);

                    if (manifest is null)
                    {
                        ReportError($"No valid {BackupManifest.FileName} found in {selectedPath}. Select a single dated backup folder created by this tool (for example ...\\Game\\staging\\2024-01-31_12-00-00).");
                        return;
                    }

                    ShowRestoreOptions(selectedPath, manifest);
                });

        private void ShowRestoreOptions(string folder, BackupManifest manifest)
        {
            _restoreManifest = manifest;

            RestoreHasStaging = manifest.Units.Any(unit => unit.Component == BackupComponent.StagingFolders);
            RestoreHasGameConfigs = manifest.Units.Any(unit => unit.Component == BackupComponent.GameConfigs);
            RestoreHasGameAdditions = manifest.Units.Any(unit => unit.Component == BackupComponent.GameAdditions);
            RestoreHasGameChanges = manifest.Units.Any(unit => unit.Component == BackupComponent.GameChanges);
            RestoreHasAppData = manifest.Units.Any(unit => unit.Component == BackupComponent.VortexAppData);
            RestoreHasCustomFolders = manifest.Units.Any(unit => unit.Component == BackupComponent.CustomFolders);

            // A switch turned off for the backup chosen before must not silently carry over to this one.
            RestoreStaging = true;
            RestoreGameConfigs = true;
            RestoreGameAdditions = true;
            RestoreGameChanges = true;
            RestoreAppData = true;
            RestoreCustomFolders = true;

            var backupName = VortexAppDataBackup.IsSharedBackup(manifest.ProfileId)
                ? $"{VortexAppDataBackup.DisplayName} (all games)"
                : manifest.GameName;

            RestoreSummary = $"{backupName}, created {manifest.CreatedAt:g}{(manifest.IsMonolithic ? ", single archive" : string.Empty)}";
            ApplyRestoreDriveDefaults();

            RefreshRestorePlan();
            RestoreFolderPath = folder;
            ReportInfo($"Backup selected for restore: {RestoreSummary} ({folder}).");
        }

        private void ClearChosenBackup()
        {
            _restoreManifest = null;
            RestoreFolderPath = null;
            RestoreSummary = null;
            RestorePlanLines.Clear();
        }

        [RelayCommand]
        private async Task RestoreAsync()
        {
            var plan = BuildCurrentPlan();

            if (_restoreManifest is null || RestoreFolderPath is null || plan.Count == 0)
            {
                ReportError("Restore: select a backup folder and at least one component to restore.");
                return;
            }

            var restoresAppData = plan.Any(item => item.Unit.Component == BackupComponent.VortexAppData);

            if (restoresAppData && _vortexProcessService.IsVortexRunning())
            {
                ReportError("Restore: Vortex is running. Close Vortex before restoring the Vortex AppData folder: it keeps its database open and would overwrite the restored data.");
                return;
            }

            var destinations = string.Join(Environment.NewLine, plan.Select(item => item.RootPath).Distinct(StringComparer.OrdinalIgnoreCase));
            var appDataNote = restoresAppData
                ? "\n\nThe Vortex AppData folder is shared by all games: Vortex's mod lists, profiles, load orders and settings of EVERY game return to the state of this backup."
                : string.Empty;

            var confirmed = _confirmationService.Confirm(
                "Restore backup",
                $"Files in these locations will be overwritten:\n{destinations}{appDataNote}\n\nFiles that would be replaced are saved to a Safety Backup first. Continue?");

            if (!confirmed)
                return;

            var folder = RestoreFolderPath;
            var summary = RestoreSummary;
            var planLines = RestorePlanLines.ToList();
            var ignorePatterns = GetRestoreIgnorePatterns(_restoreManifest);

            await RunOperationAsync("Restore", async scope =>
            {
                ReportInfo($"Restoring {summary} from {folder}");

                foreach (var line in planLines)
                    ReportInfo("    " + line);

                var result = await Task.Run(() => _restoreService.RestoreAsync(folder, plan, scope, scope.CancellationToken, ignorePatterns), scope.CancellationToken);

                if (result.IgnoredFiles > 0)
                    ReportInfo($"Game folder ignore list: {result.IgnoredFiles:N0} file(s) in the backup match the ignore patterns and were not restored.");

                if (result.SafetyBackupPath is not null)
                    ReportInfo($"Files that were replaced were saved to {result.SafetyBackupPath}");

                ReportSuccess($"Restore finished: {result.FilesRestored:N0} file(s) restored.");

                if (result.FailedPaths.Count > 0)
                    ReportFailedFiles(result.FailedPaths, "file(s) could not be restored (in use or access denied)");
            });
        }

        /// <summary>
        /// The global ignore patterns apply to a restore exactly like to a backup: only when the matching profile uses the
        /// ignore list (a backup without a matching profile uses it too, the default for profiles).
        /// </summary>
        private IReadOnlyCollection<string> GetRestoreIgnorePatterns(BackupManifest? manifest)
        {
            if (manifest is null || _settings is null)
                return [];

            var profile = _allProfiles.FirstOrDefault(candidate => candidate.Id == manifest.ProfileId)
                ?? _allProfiles.FirstOrDefault(candidate => string.Equals(candidate.GameName, manifest.GameName, StringComparison.OrdinalIgnoreCase));

            return profile is { UseIgnoreList: false } ? [] : _settings.GlobalIgnorePatterns;
        }

        private List<RestorePlanItem> BuildCurrentPlan()
        {
            if (_restoreManifest is null)
                return [];

            var components = new List<BackupComponent>();

            if (RestoreHasStaging && RestoreStaging)
                components.Add(BackupComponent.StagingFolders);

            if (RestoreHasGameConfigs && RestoreGameConfigs)
                components.Add(BackupComponent.GameConfigs);

            if (RestoreHasGameAdditions && RestoreGameAdditions)
                components.Add(BackupComponent.GameAdditions);

            if (RestoreHasGameChanges && RestoreGameChanges)
                components.Add(BackupComponent.GameChanges);

            if (RestoreHasAppData && RestoreAppData)
                components.Add(BackupComponent.VortexAppData);

            if (RestoreHasCustomFolders && RestoreCustomFolders)
                components.Add(BackupComponent.CustomFolders);

            var adjustedProfiles = ApplyRestoreDriveOverrides(_allProfiles);
            var appDataPath = ReplaceDriveIfRequested(_settings?.VortexAppDataPath, RestoreAppDataDrive);

            return _restoreService.BuildPlan(_restoreManifest, adjustedProfiles, components, appDataPath).ToList();
        }

        private void ApplyRestoreDriveDefaults()
        {
            if (_restoreManifest is null)
                return;

            var defaultPlan = _restoreService.BuildPlan(_restoreManifest, _allProfiles, [
                BackupComponent.StagingFolders,
                BackupComponent.GameConfigs,
                BackupComponent.GameAdditions,
                BackupComponent.GameChanges,
                BackupComponent.VortexAppData,
                BackupComponent.CustomFolders], _settings?.VortexAppDataPath).ToList();

            RestoreStagingDrive = defaultPlan.FirstOrDefault(item => item.Unit.Component == BackupComponent.StagingFolders) is { } staging
                ? _pathDriveService.GetDrive(staging.RootPath)
                : null;
            RestoreGameConfigsDrive = defaultPlan.FirstOrDefault(item => item.Unit.Component is BackupComponent.GameConfigs or BackupComponent.GameAdditions or BackupComponent.GameChanges) is { } configs
                ? _pathDriveService.GetDrive(configs.RootPath)
                : null;
            RestoreAppDataDrive = defaultPlan.FirstOrDefault(item => item.Unit.Component == BackupComponent.VortexAppData) is { } appData
                ? _pathDriveService.GetDrive(appData.RootPath)
                : null;
            RestoreCustomFoldersDrive = defaultPlan.FirstOrDefault(item => item.Unit.Component == BackupComponent.CustomFolders) is { } custom
                ? _pathDriveService.GetDrive(custom.RootPath)
                : null;
        }

        private IReadOnlyList<BackupProfile> ApplyRestoreDriveOverrides(IReadOnlyList<BackupProfile> profiles)
        {
            return profiles.Select(profile => profile with
            {
                VortexStagingPath = ReplaceDriveIfRequested(profile.VortexStagingPath, RestoreStagingDrive),
                GameRootPath = ReplaceDriveIfRequested(profile.GameRootPath, RestoreGameConfigsDrive),
                CustomFolders = profile.CustomFolders.Select(path => ReplaceDriveIfRequested(path, RestoreCustomFoldersDrive)).ToList()
            }).ToList();
        }

        private string? ReplaceDriveIfRequested(string? path, string? selectedDrive)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(selectedDrive))
                return path;

            var currentDrive = _pathDriveService.GetDrive(path);

            if (currentDrive is null || string.Equals(currentDrive, selectedDrive, StringComparison.OrdinalIgnoreCase))
                return path;

            return _pathDriveService.ReplaceDrive(path, selectedDrive);
        }

        private void RefreshRestorePlan()
        {
            RestorePlanLines.Clear();

            foreach (var group in BuildCurrentPlan().GroupBy(item => (item.Unit.Component, item.RootPath, item.UsesOriginalPath)))
            {
                var label = GetComponentLabel(group.Key.Component);
                var note = group.Key.UsesOriginalPath ? " (path from the backup, no matching profile path)" : string.Empty;

                RestorePlanLines.Add($"{label}: {group.Count()} archive part(s) → {group.Key.RootPath}{note}");
            }
        }
    }
}