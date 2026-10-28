using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using UniversalBackupTool.Interface.Interfaces.Backups;
using UniversalBackupTool.Interface.Interfaces.Dialogs;
using UniversalBackupTool.Interface.Interfaces.Jobs;
using UniversalBackupTool.Interface.Interfaces.Profiles;
using UniversalBackupTool.Interface.Interfaces.Settings;
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

        private AppSettings? _settings;
        private List<BackupProfile> _allProfiles = [];
        private BackupManifest? _restoreManifest;
        private CancellationTokenSource? _cancellation;

        public BackupRestoreViewModel(
            IProfileStore profileStore,
            ISettingsStore settingsStore,
            IAppStoragePathsProvider storagePathsProvider,
            IBackupJobRunner backupJobRunner,
            IBackupJobStateService backupJobStateService,
            IRestoreService restoreService,
            IFolderPickerService folderPickerService,
            IConfirmationService confirmationService)
        {
            _profileStore = profileStore ?? throw new ArgumentNullException(nameof(profileStore));
            _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
            _storagePathsProvider = storagePathsProvider ?? throw new ArgumentNullException(nameof(storagePathsProvider));
            _backupJobRunner = backupJobRunner ?? throw new ArgumentNullException(nameof(backupJobRunner));
            _backupJobStateService = backupJobStateService ?? throw new ArgumentNullException(nameof(backupJobStateService));
            _restoreService = restoreService ?? throw new ArgumentNullException(nameof(restoreService));
            _folderPickerService = folderPickerService ?? throw new ArgumentNullException(nameof(folderPickerService));
            _confirmationService = confirmationService ?? throw new ArgumentNullException(nameof(confirmationService));

            PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(IsBusy))
                    OnPropertyChanged(nameof(IsIdle));
            };
        }

        public ObservableCollection<BackupTargetItemViewModel> Targets { get; } = [];

        public ObservableCollection<string> RestorePlanLines { get; } = [];

        public bool IsIdle => !IsBusy;

        public bool HasNoProfiles => Targets.Count == 0;

        public bool HasPausedJob => PausedJob is not null;

        public bool HasStatusMessage => !string.IsNullOrEmpty(StatusMessage);

        public bool HasWarningMessage => !string.IsNullOrEmpty(WarningMessage);

        public bool HasRestoreManifest => RestoreSummary is not null;

        public string PausedJobMessage => PausedJob is null
            ? string.Empty
            : $"A previous backup ({Path.GetFileName(PausedJob.ArchivePath)}) is incomplete. Files that were in use: {PausedJob.FailedFiles.Count}. Close the game and Vortex, then resume.";

        [ObservableProperty]
        private bool _includeStaging = true;

        [ObservableProperty]
        private bool _includeGameConfigs;

        [ObservableProperty]
        private bool _includeVortexAppData = true;

        [ObservableProperty]
        private bool _includeCustomFolders;

        [ObservableProperty]
        private double _progressPercent;

        [ObservableProperty]
        private string? _progressText;

        [ObservableProperty]
        private string? _currentItemText;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
        private string? _statusMessage;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasWarningMessage))]
        private string? _warningMessage;

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
        private bool _restoreHasAppData;

        [ObservableProperty]
        private bool _restoreHasCustomFolders;

        [ObservableProperty]
        private bool _restoreStaging = true;

        [ObservableProperty]
        private bool _restoreGameConfigs = true;

        [ObservableProperty]
        private bool _restoreAppData = true;

        [ObservableProperty]
        private bool _restoreCustomFolders = true;

        partial void OnRestoreStagingChanged(bool value) => RefreshRestorePlan();

        partial void OnRestoreGameConfigsChanged(bool value) => RefreshRestorePlan();

        partial void OnRestoreAppDataChanged(bool value) => RefreshRestorePlan();

        partial void OnRestoreCustomFoldersChanged(bool value) => RefreshRestorePlan();

        [RelayCommand]
        private Task LoadAsync() => RunBusyAsync(async () =>
                {
                    ClearMessages();

                    var paths = _storagePathsProvider.GetPaths();

                    _settings = await _settingsStore.LoadAsync(paths.SettingsFilePath);
                    _allProfiles = (await _profileStore.LoadAsync(paths.ProfilesFilePath)).ToList();

                    var previouslySelected = Targets.Where(target => target.IsSelected).Select(target => target.Profile.Id).ToHashSet();

                    Targets.Clear();

                    foreach (var profile in _allProfiles)
                        Targets.Add(new BackupTargetItemViewModel(profile, previouslySelected.Contains(profile.Id)));

                    OnPropertyChanged(nameof(HasNoProfiles));

                    var job = await _backupJobStateService.LoadAsync(paths.ActiveBackupJobFilePath);

                    PausedJob = job is { IsCompleted: false } ? job : null;

                    RefreshRestorePlan();
                });

        [RelayCommand]
        private Task StartBackupAsync() => RunOperationAsync(async token =>
                {
                    var selected = Targets.Where(target => target.IsSelected).Select(target => target.Profile).ToList();
                    var options = new BackupOptions(IncludeStaging, IncludeGameConfigs, IncludeVortexAppData, IncludeCustomFolders);

                    if (selected.Count == 0)
                    {
                        ErrorMessage = "Select at least one profile.";
                        return;
                    }

                    if (!options.HasAnyComponent)
                    {
                        ErrorMessage = "Select at least one component to back up.";
                        return;
                    }

                    if (_settings is null || string.IsNullOrWhiteSpace(_settings.GoogleDriveBackupPath))
                    {
                        ErrorMessage = "The backup destination folder is not configured. Set it on the Settings page.";
                        return;
                    }

                    if (PausedJob is not null)
                    {
                        if (!_confirmationService.Confirm("Paused job exists", "A previous backup is incomplete. Starting a new backup discards it (the already created archives stay on disk). Continue?"))
                            return;

                        await DiscardPausedJobCoreAsync();
                    }

                    var settings = _settings;
                    var progress = CreateProgress();

                    var batch = await Task.Run(() => _backupJobRunner.RunBackupAsync(selected, options, settings, progress, token), token);

                    await ReloadPausedJobAsync();

                    StatusMessage = $"Backup finished for {batch.Results.Count} profile(s): {batch.ModsArchived:N0} changed/new mod(s) in this snapshot, {batch.ModsReused:N0} unchanged mod archive(s) reused, {batch.ModsRemoved:N0} removed mod(s) omitted from the new snapshot; {batch.FilesArchived:N0} file(s) written to {settings.GoogleDriveBackupPath}.";
                    WarningMessage = BuildWarning(batch);
                });

        [RelayCommand]
        private Task ResumeAsync() => RunOperationAsync(async token =>
                {
                    if (PausedJob is null || _settings is null)
                        return;

                    var job = PausedJob;
                    var settings = _settings;
                    var progress = CreateProgress();

                    var batch = await Task.Run(() => _backupJobRunner.ResumeBackupAsync(job, settings, progress, token), token);

                    await ReloadPausedJobAsync();

                    if (batch.IsComplete)
                    {
                        StatusMessage = $"Resume finished: {batch.FilesArchived:N0} missing file(s) were appended. The backup is complete.";
                    }
                    else
                    {
                        StatusMessage = $"{batch.FilesArchived:N0} file(s) were appended.";
                        WarningMessage = $"{batch.FailedFiles.Count} file(s) are still in use. Close the game or Vortex and resume again.";
                    }
                });

        [RelayCommand]
        private async Task DiscardPausedJobAsync()
        {
            if (PausedJob is null)
                return;

            if (!_confirmationService.Confirm("Discard paused job", "The locked files will not be added to that backup. Discard the paused job?"))
                return;

            await DiscardPausedJobCoreAsync();
        }

        [RelayCommand]
        private void Cancel() => _cancellation?.Cancel();

        [RelayCommand]
        private Task BrowseRestoreFolderAsync() => RunBusyAsync(async () =>
                {
                    ClearMessages();

                    var selectedPath = _folderPickerService.PickFolder(RestoreFolderPath ?? _settings?.GoogleDriveBackupPath);

                    if (string.IsNullOrWhiteSpace(selectedPath))
                        return;

                    var manifest = await _restoreService.ReadManifestAsync(selectedPath);

                    if (manifest is null)
                    {
                        ErrorMessage = $"No valid {BackupManifest.FileName} found in this folder. Select a single backup folder created by this tool (for example .../Game/2024-01-31_12-00-00).";
                        return;
                    }

                    RestoreFolderPath = selectedPath;
                    _restoreManifest = manifest;

                    RestoreHasStaging = manifest.Units.Any(unit => unit.Component == BackupComponent.StagingFolders);
                    RestoreHasGameConfigs = manifest.Units.Any(unit => unit.Component == BackupComponent.GameConfigs);
                    RestoreHasAppData = manifest.Units.Any(unit => unit.Component == BackupComponent.VortexAppData);
                    RestoreHasCustomFolders = manifest.Units.Any(unit => unit.Component == BackupComponent.CustomFolders);

                    RestoreSummary = $"{manifest.GameName}, created {manifest.CreatedAt:g}{(manifest.IsMonolithic ? ", single archive" : string.Empty)}";

                    RefreshRestorePlan();
                });

        [RelayCommand]
        private Task RestoreAsync() => RunOperationAsync(async token =>
                {
                    var plan = BuildCurrentPlan();

                    if (_restoreManifest is null || RestoreFolderPath is null || plan.Count == 0)
                    {
                        ErrorMessage = "Select a backup folder and at least one component to restore.";
                        return;
                    }

                    var destinations = string.Join(Environment.NewLine, plan.Select(item => item.RootPath).Distinct(StringComparer.OrdinalIgnoreCase));

                    var confirmed = _confirmationService.Confirm(
                        "Restore backup",
                        $"Files in these locations will be overwritten:\n{destinations}\n\nFiles that would be replaced are saved to a Safety Backup first. Continue?");

                    if (!confirmed)
                        return;

                    var folder = RestoreFolderPath;
                    var progress = CreateProgress();

                    var result = await Task.Run(() => _restoreService.RestoreAsync(folder, plan, progress, token), token);

                    StatusMessage = $"Restore finished: {result.FilesRestored:N0} file(s) restored."
                        + (result.SafetyBackupPath is null ? string.Empty : $" Replaced files were saved to {result.SafetyBackupPath}.");

                    if (result.FailedPaths.Count > 0)
                    {
                        WarningMessage = $"{result.FailedPaths.Count} file(s) could not be restored (in use or access denied), for example: {result.FailedPaths[0]}";
                    }
                });

        private Task RunOperationAsync(Func<CancellationToken, Task> operation) => RunBusyAsync(async () =>
        {
            ClearMessages();

            ProgressPercent = 0;
            ProgressText = null;
            CurrentItemText = null;

            using var cancellation = new CancellationTokenSource();
            _cancellation = cancellation;

            try
            {
                await operation(cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                WarningMessage = "The operation was cancelled.";
            }
            finally
            {
                _cancellation = null;
            }
        });

        private Progress<BackupProgress> CreateProgress()
        {
            return new Progress<BackupProgress>(value =>
            {
                ProgressPercent = value.Fraction * 100;
                ProgressText = $"{value.Phase}: {value.FilesProcessed:N0} / {value.TotalFiles:N0} files ({FormatBytes(value.BytesProcessed)} / {FormatBytes(value.TotalBytes)})";
                CurrentItemText = value.CurrentItem;
            });
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

        private static string? BuildWarning(BackupBatchResult batch)
        {
            var lines = batch.Results.SelectMany(result => result.Warnings).ToList();

            if (batch.FailedFiles.Count > 0)
                lines.Add($"{batch.FailedFiles.Count} file(s) were in use and are missing from the archives. Close the game or Vortex, then select Continue Backup below.");

            return lines.Count == 0 ? null : string.Join(Environment.NewLine, lines);
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

            if (RestoreHasAppData && RestoreAppData)
                components.Add(BackupComponent.VortexAppData);

            if (RestoreHasCustomFolders && RestoreCustomFolders)
                components.Add(BackupComponent.CustomFolders);

            return _restoreService.BuildPlan(_restoreManifest, _allProfiles, components).ToList();
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
                    BackupComponent.VortexAppData => "Vortex AppData",
                    _ => "Custom folder"
                };

                var note = group.Key.UsesOriginalPath ? " (path from the backup, no matching profile path)" : string.Empty;

                RestorePlanLines.Add($"{label}: {group.Count()} archive part(s) → {group.Key.RootPath}{note}");
            }
        }

        private void ClearMessages()
        {
            ErrorMessage = null;
            StatusMessage = null;
            WarningMessage = null;
        }

        private static string FormatBytes(long bytes)
        {
            string[] units = ["B", "KB", "MB", "GB", "TB"];
            double value = bytes;
            var unit = 0;

            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            return $"{value:0.#} {units[unit]}";
        }
    }
}