using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using UniversalBackupTool.Interface.Interfaces.Backups;
using UniversalBackupTool.Interface.Interfaces.Dialogs;
using UniversalBackupTool.Interface.Interfaces.Jobs;
using UniversalBackupTool.Interface.Interfaces.Operations;
using UniversalBackupTool.Interface.Interfaces.Paths;
using UniversalBackupTool.Interface.Interfaces.Profiles;
using UniversalBackupTool.Interface.Interfaces.Settings;
using UniversalBackupTool.Interface.Interfaces.Vortex;
using UniversalBackupTool.Interfaces.Storage;
using UniversalBackupTool.Model.Models.Backups;
using UniversalBackupTool.Model.Models.Jobs;
using UniversalBackupTool.Model.Models.Profiles;
using UniversalBackupTool.Model.Models.Settings;
using UniversalBackupTool.ViewModel.ViewModels.Base;

namespace UniversalBackupTool.ViewModel.ViewModels.BackupRestore
{
    public sealed partial class BackupRestoreViewModel : BasicViewModel
    {
        private readonly IProfileStore _profileStore;
        private readonly ISettingsStore _settingsStore;
        private readonly IAppStoragePathsProvider _storagePathsProvider;
        private readonly IBackupJobRunner _backupJobRunner;
        private readonly IBackupJobStateService _backupJobStateService;
        private readonly IRestoreService _restoreService;
        private readonly IFolderPickerService _folderPickerService;
        private readonly IConfirmationService _confirmationService;
        private readonly IPathDriveService _pathDriveService;
        private readonly IVortexProcessService _vortexProcessService;

        private const int MaxListedPaths = 20;

        private AppSettings? _settings;
        private List<BackupProfile> _allProfiles = [];
        private BackupManifest? _restoreManifest;

        public BackupRestoreViewModel(
            IProfileStore profileStore,
            ISettingsStore settingsStore,
            IAppStoragePathsProvider storagePathsProvider,
            IBackupJobRunner backupJobRunner,
            IBackupJobStateService backupJobStateService,
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
            _backupJobRunner = backupJobRunner ?? throw new ArgumentNullException(nameof(backupJobRunner));
            _backupJobStateService = backupJobStateService ?? throw new ArgumentNullException(nameof(backupJobStateService));
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

        public ObservableCollection<BackupTargetItemViewModel> Targets { get; } = [];

        /// <summary>
        /// Targets matching <see cref="TargetFilter"/>; this is what the profile picker shows. Selection lives on the items,
        /// so filtering never changes what is selected.
        /// </summary>
        public ObservableCollection<BackupTargetItemViewModel> VisibleTargets { get; } = [];

        [ObservableProperty]
        private string _targetFilter = string.Empty;

        partial void OnTargetFilterChanged(string value) => RefreshVisibleTargets();

        public bool HasNoVisibleTargets => Targets.Count > 0 && VisibleTargets.Count == 0;

        public string SelectedTargetsSummary
        {
            get
            {
                var selected = Targets.Count(target => target.IsSelected);

                return Targets.Count == 0
                    ? "No profiles yet. Add one on the Profiles page."
                    : selected == 0
                        ? $"None of {Targets.Count} selected"
                        : $"{selected} of {Targets.Count} selected";
            }
        }

        /// <summary>
        /// True when every listed game (the filtered ones while searching) is selected; the button then deselects them.
        /// </summary>
        public bool AreAllVisibleTargetsSelected => VisibleTargets.Count > 0 && VisibleTargets.All(target => target.IsSelected);

        public string SelectAllButtonText => AreAllVisibleTargetsSelected ? "Deselect all" : "Select all";

        [RelayCommand]
        private void ToggleSelectAllTargets()
        {
            // Acts on the games currently listed, so with a search it only affects the matching ones.
            var select = !AreAllVisibleTargetsSelected;

            foreach (var target in VisibleTargets)
                target.IsSelected = select;
        }

        [RelayCommand]
        private void ClearTargetSelection()
        {
            foreach (var target in Targets)
                target.IsSelected = false;
        }

        private void OnTargetPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(BackupTargetItemViewModel.IsSelected))
                NotifySelectionChanged();
        }

        private void NotifySelectionChanged()
        {
            OnPropertyChanged(nameof(SelectedTargetsSummary));
            OnPropertyChanged(nameof(AreAllVisibleTargetsSelected));
            OnPropertyChanged(nameof(SelectAllButtonText));
            OnPropertyChanged(nameof(ShowVortexComponents));
        }

        /// <summary>
        /// The Vortex components (staging, Vortex AppData) are offered while no game is selected or when at least one
        /// selected game is managed by Vortex. With only non-Vortex games selected they are hidden and not backed up.
        /// </summary>
        public bool ShowVortexComponents
        {
            get
            {
                var selected = Targets.Where(target => target.IsSelected).ToList();

                return selected.Count == 0 || selected.Any(target => target.Profile.UsesVortex);
            }
        }

        private void RefreshVisibleTargets()
        {
            var filter = TargetFilter.Trim();

            VisibleTargets.Clear();

            foreach (var target in Targets.Where(target => filter.Length == 0 || target.GameName.Contains(filter, StringComparison.CurrentCultureIgnoreCase)))
                VisibleTargets.Add(target);

            OnPropertyChanged(nameof(HasNoVisibleTargets));
            NotifySelectionChanged();
        }

        public ObservableCollection<string> RestorePlanLines { get; } = [];

        public IReadOnlyList<string> AvailableDrives { get; } = DriveInfo.GetDrives()
            .Where(drive => drive.IsReady)
            .Select(drive => drive.Name[..2])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(drive => drive, StringComparer.OrdinalIgnoreCase)
            .ToList();

        public bool HasNoProfiles => Targets.Count == 0;

        public bool HasPausedJob => PausedJob is not null;

        public bool HasRestoreManifest => RestoreSummary is not null;

        public string PausedJobMessage => PausedJob is null
            ? string.Empty
            : $"A previous backup ({Path.GetFileName(PausedJob.ArchivePath)}) is incomplete. Files that were in use: {PausedJob.FailedFiles.Count}. Close the game and Vortex, then resume.";

        [ObservableProperty]
        private bool _includeStaging;

        [ObservableProperty]
        private bool _includeGameConfigs;

        [ObservableProperty]
        private bool _includeGameChanges;

        [ObservableProperty]
        private bool _includeVortexAppData;

        [ObservableProperty]
        private bool _includeCustomFolders;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasPausedJob))]
        [NotifyPropertyChangedFor(nameof(PausedJobMessage))]
        private ActiveBackupJob? _pausedJob;

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
        /// Drive of the game folder, shared by both game folder components (Game configs and Changed game files).
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

        partial void OnRestoreGameChangesChanged(bool value) => RefreshRestorePlan();

        partial void OnRestoreAppDataChanged(bool value) => RefreshRestorePlan();

        partial void OnRestoreCustomFoldersChanged(bool value) => RefreshRestorePlan();

        [RelayCommand]
        private Task LoadAsync() => RunBusyAsync(async () =>
                {
                    var paths = _storagePathsProvider.GetPaths();

                    _settings = await _settingsStore.LoadAsync(paths.SettingsFilePath);
                    _allProfiles = (await _profileStore.LoadAsync(paths.ProfilesFilePath)).ToList();

                    var previouslySelected = Targets.Where(target => target.IsSelected).Select(target => target.Profile.Id).ToHashSet();

                    foreach (var target in Targets)
                        target.PropertyChanged -= OnTargetPropertyChanged;

                    Targets.Clear();

                    foreach (var profile in _allProfiles.OrderBy(profile => profile.GameName, StringComparer.CurrentCultureIgnoreCase))
                    {
                        var target = new BackupTargetItemViewModel(profile, previouslySelected.Contains(profile.Id));
                        target.PropertyChanged += OnTargetPropertyChanged;
                        Targets.Add(target);
                    }

                    OnPropertyChanged(nameof(HasNoProfiles));
                    RefreshVisibleTargets();

                    var job = await _backupJobStateService.LoadAsync(paths.ActiveBackupJobFilePath);

                    PausedJob = job is { IsCompleted: false } ? job : null;

                    RefreshRestorePlan();
                });

        [RelayCommand]
        private async Task StartBackupAsync()
        {
            var selected = Targets.Where(target => target.IsSelected).Select(target => target.Profile).ToList();
            // Hidden Vortex toggles (only non-Vortex games selected) keep their value for later but are not used.
            var vortex = ShowVortexComponents;
            var options = new BackupOptions(
                IncludeStaging && vortex,
                IncludeGameConfigs,
                IncludeVortexAppData && vortex,
                IncludeCustomFolders,
                IncludeGameChanges: IncludeGameChanges);

            if (selected.Count == 0)
            {
                ReportError("Backup: select at least one profile.");
                return;
            }

            if (!options.HasAnyComponent)
            {
                ReportError("Backup: select at least one component to back up.");
                return;
            }

            if (_settings is null || string.IsNullOrWhiteSpace(_settings.GoogleDriveBackupPath))
            {
                ReportError("Backup: the backup destination folder is not configured. Set it on the Settings page.");
                return;
            }

            if (options.IncludeVortexAppData && _vortexProcessService.IsVortexRunning()
                && !_confirmationService.Confirm(
                    "Vortex is running",
                    "Vortex is running. It keeps its database in the AppData folder open and keeps writing to it, so the Vortex AppData backup may be inconsistent or miss locked files. Close Vortex first for a reliable backup.\n\nBack up anyway?"))
                return;

            if (PausedJob is not null)
            {
                if (!_confirmationService.Confirm("Paused job exists", "A previous backup is incomplete. Starting a new backup discards it (the already created archives stay on disk). Continue?"))
                    return;

                await DiscardPausedJobCoreAsync();
                ReportWarning("The paused backup job was discarded.");
            }

            var settings = _settings;

            await RunOperationAsync("Backup", async scope =>
            {
                ReportInfo($"Profiles: {string.Join(", ", selected.Select(profile => profile.GameName))}. Components: {DescribeComponents(options)}. Destination: {settings.GoogleDriveBackupPath}");

                var batch = await Task.Run(() => _backupJobRunner.RunBackupAsync(selected, options, settings, scope, scope.CancellationToken), scope.CancellationToken);

                await ReloadPausedJobAsync();
                ReportBackupResult(batch, settings);

                // Only the games are deselected, after the backup ran to the end (also when some files were locked and
                // Continue Backup is offered); the component toggles stay as they are. A cancelled or failed backup keeps
                // the games selected so it can be retried.
                ClearTargetSelection();
            });
        }

        [RelayCommand]
        private async Task ResumeAsync()
        {
            if (PausedJob is null || _settings is null)
                return;

            var job = PausedJob;
            var settings = _settings;

            await RunOperationAsync("Continue Backup", async scope =>
            {
                ReportInfo($"Retrying {job.FailedFiles.Count:N0} file(s) that were in use.");

                var batch = await Task.Run(() => _backupJobRunner.ResumeBackupAsync(job, settings, scope, scope.CancellationToken), scope.CancellationToken);

                await ReloadPausedJobAsync();

                if (batch.IsComplete)
                {
                    ReportSuccess($"Continue Backup finished: {batch.FilesArchived:N0} missing file(s) were added. The backup is complete.");
                }
                else
                {
                    ReportInfo($"Continue Backup: {batch.FilesArchived:N0} file(s) were added.");
                    ReportFailedFiles(batch.FailedFiles.Select(file => file.SourcePath).ToList(), "file(s) are still in use. Close the game or Vortex and select Continue Backup again");
                }
            });
        }

        [RelayCommand]
        private async Task DiscardPausedJobAsync()
        {
            if (PausedJob is null || !CanStartOperation)
                return;

            if (!_confirmationService.Confirm("Discard paused job", "The locked files will not be added to that backup. Discard the paused job?"))
                return;

            await DiscardPausedJobCoreAsync();
            ReportWarning("The paused backup job was discarded.");
        }

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

                    RestoreFolderPath = selectedPath;
                    _restoreManifest = manifest;

                    RestoreHasStaging = manifest.Units.Any(unit => unit.Component == BackupComponent.StagingFolders);
                    RestoreHasGameConfigs = manifest.Units.Any(unit => unit.Component == BackupComponent.GameConfigs);
                    RestoreHasGameChanges = manifest.Units.Any(unit => unit.Component == BackupComponent.GameChanges);
                    RestoreHasAppData = manifest.Units.Any(unit => unit.Component == BackupComponent.VortexAppData);
                    RestoreHasCustomFolders = manifest.Units.Any(unit => unit.Component == BackupComponent.CustomFolders);

                    var backupName = VortexAppDataBackup.IsSharedBackup(manifest.ProfileId)
                        ? $"{VortexAppDataBackup.DisplayName} (all games)"
                        : manifest.GameName;

                    RestoreSummary = $"{backupName}, created {manifest.CreatedAt:g}{(manifest.IsMonolithic ? ", single archive" : string.Empty)}";
                    ApplyRestoreDriveDefaults();

                    RefreshRestorePlan();
                    ReportInfo($"Backup selected for restore: {RestoreSummary} ({selectedPath}).");
                });

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

        private void ReportBackupResult(BackupBatchResult batch, AppSettings settings)
        {
            foreach (var result in batch.Results)
            {
                ReportInfo($"{result.GameName}: {result.FilesArchived:N0} file(s) written, snapshot {result.BackupDirectory}");

                if (result.IgnoredFiles > 0)
                    ReportInfo($"{result.GameName}: game folder ignore list skipped {result.IgnoredFiles:N0} file(s), e.g. {string.Join(", ", result.IgnoredExamples)}");
            }

            foreach (var warning in batch.Results.SelectMany(result => result.Warnings).Concat(batch.Warnings))
                ReportWarning(warning);

            ReportSuccess($"Backup finished: {batch.ModsArchived:N0} changed/new mod(s) archived, {batch.ModsReused:N0} unchanged mod archive(s) reused, {batch.ModsRemoved:N0} removed mod(s) omitted from the new snapshot; {batch.FilesArchived:N0} file(s) written to {settings.GoogleDriveBackupPath}.");

            if (batch.FailedFiles.Count > 0)
                ReportFailedFiles(batch.FailedFiles.Select(file => file.SourcePath).ToList(), "file(s) were in use and are missing from the archives. Close the game or Vortex, then select Continue Backup on the Backup & Restore page");
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

        private void ReportFailedFiles(IReadOnlyList<string> paths, string description)
        {
            ReportWarning($"{paths.Count:N0} {description}:");

            foreach (var path in paths.Take(MaxListedPaths))
                ReportWarning("    " + path);

            if (paths.Count > MaxListedPaths)
                ReportWarning($"    ... and {paths.Count - MaxListedPaths:N0} more.");
        }

        private static string DescribeComponents(BackupOptions options)
        {
            var names = new List<string>();

            if (options.IncludeStaging)
                names.Add("staging");

            if (options.IncludeGameConfigs)
                names.Add("game configs");

            if (options.IncludeGameChanges)
                names.Add("changed game files");

            if (options.IncludeVortexAppData)
                names.Add("Vortex AppData");

            if (options.IncludeCustomFolders)
                names.Add("custom paths");

            return string.Join(", ", names);
        }

        private async Task DiscardPausedJobCoreAsync()
        {
            await _backupJobStateService.DeleteAsync(_storagePathsProvider.GetPaths().ActiveBackupJobFilePath);

            PausedJob = null;
        }

        private async Task ReloadPausedJobAsync()
        {
            var job = await _backupJobStateService.LoadAsync(_storagePathsProvider.GetPaths().ActiveBackupJobFilePath);

            PausedJob = job is { IsCompleted: false } ? job : null;
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
                BackupComponent.GameChanges,
                BackupComponent.VortexAppData,
                BackupComponent.CustomFolders], _settings?.VortexAppDataPath).ToList();

            RestoreStagingDrive = defaultPlan.FirstOrDefault(item => item.Unit.Component == BackupComponent.StagingFolders) is { } staging
                ? _pathDriveService.GetDrive(staging.RootPath)
                : null;
            RestoreGameConfigsDrive = defaultPlan.FirstOrDefault(item => item.Unit.Component is BackupComponent.GameConfigs or BackupComponent.GameChanges) is { } configs
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
                var label = group.Key.Component switch
                {
                    BackupComponent.StagingFolders => "Staging folders",
                    BackupComponent.GameConfigs => "Game configs",
                    BackupComponent.GameChanges => "Changed game files",
                    BackupComponent.VortexAppData => "Vortex AppData",
                    _ => "Custom path"
                };

                var note = group.Key.UsesOriginalPath ? " (path from the backup, no matching profile path)" : string.Empty;

                RestorePlanLines.Add($"{label}: {group.Count()} archive part(s) → {group.Key.RootPath}{note}");
            }
        }
    }
}