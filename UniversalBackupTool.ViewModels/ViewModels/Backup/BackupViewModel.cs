using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using UniversalBackupTool.Interface.Interfaces.Backups;
using UniversalBackupTool.Interface.Interfaces.Dialogs;
using UniversalBackupTool.Interface.Interfaces.Jobs;
using UniversalBackupTool.Interface.Interfaces.Operations;
using UniversalBackupTool.Interface.Interfaces.Profiles;
using UniversalBackupTool.Interface.Interfaces.Settings;
using UniversalBackupTool.Interface.Interfaces.Vortex;
using UniversalBackupTool.Interfaces.Storage;
using UniversalBackupTool.Model.Models.Backups;
using UniversalBackupTool.Model.Models.Jobs;
using UniversalBackupTool.Model.Models.Profiles;
using UniversalBackupTool.Model.Models.Settings;
using UniversalBackupTool.ViewModel.ViewModels.Base;

namespace UniversalBackupTool.ViewModel.ViewModels.Backup
{
    public sealed partial class BackupViewModel : BasicViewModel
    {
        private readonly IProfileStore _profileStore;
        private readonly ISettingsStore _settingsStore;
        private readonly IAppStoragePathsProvider _storagePathsProvider;
        private readonly IBackupJobRunner _backupJobRunner;
        private readonly IBackupJobStateService _backupJobStateService;
        private readonly IConfirmationService _confirmationService;
        private readonly IVortexProcessService _vortexProcessService;
        private readonly IGamePresenceService _gamePresenceService;

        private AppSettings? _settings;
        private List<BackupProfile> _allProfiles = [];
        private int _installedCheckVersion;

        public BackupViewModel(
            IProfileStore profileStore,
            ISettingsStore settingsStore,
            IAppStoragePathsProvider storagePathsProvider,
            IBackupJobRunner backupJobRunner,
            IBackupJobStateService backupJobStateService,
            IConfirmationService confirmationService,
            IVortexProcessService vortexProcessService,
            IGamePresenceService gamePresenceService,
            IOperationCoordinator operationCoordinator,
            IOperationOutputService operationOutputService)
            : base(operationOutputService, operationCoordinator)
        {
            _profileStore = profileStore ?? throw new ArgumentNullException(nameof(profileStore));
            _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
            _storagePathsProvider = storagePathsProvider ?? throw new ArgumentNullException(nameof(storagePathsProvider));
            _backupJobRunner = backupJobRunner ?? throw new ArgumentNullException(nameof(backupJobRunner));
            _backupJobStateService = backupJobStateService ?? throw new ArgumentNullException(nameof(backupJobStateService));
            _confirmationService = confirmationService ?? throw new ArgumentNullException(nameof(confirmationService));
            _vortexProcessService = vortexProcessService ?? throw new ArgumentNullException(nameof(vortexProcessService));
            _gamePresenceService = gamePresenceService ?? throw new ArgumentNullException(nameof(gamePresenceService));
        }

        public ObservableCollection<BackupTargetItemViewModel> Targets { get; } = [];

        /// <summary>
        /// The games managed by Vortex. A backup runs on one section only (see <see cref="BackupTargetSectionViewModel"/>).
        /// </summary>
        public BackupTargetSectionViewModel VortexTargets { get; } = new("Managed by Vortex", usesVortex: true, otherSectionName: "other games");

        /// <summary>
        /// The games not managed by Vortex.
        /// </summary>
        public BackupTargetSectionViewModel OtherTargets { get; } = new("Other games", usesVortex: false, otherSectionName: "Vortex");

        [ObservableProperty]
        private string _targetFilter = string.Empty;

        partial void OnTargetFilterChanged(string value)
        {
            OnPropertyChanged(nameof(NoVisibleTargetsText));
            RefreshVisibleTargets();
        }

        public bool HasNoVisibleTargets => Targets.Count > 0 && !VortexTargets.IsVisible && !OtherTargets.IsVisible;

        public string NoVisibleTargetsText => TargetFilter.Trim().Length > 0
            ? "No games match the search."
            : "None of the games are installed on this PC. Turn off \"Show only installed games\" in Settings to list them anyway.";

        /// <summary>
        /// Which games "Show only installed games" hides and why; empty while none are hidden.
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasHiddenGamesSummary))]
        private string _hiddenGamesSummary = string.Empty;

        public bool HasHiddenGamesSummary => HiddenGamesSummary.Length > 0;

        /// <summary>
        /// True while games are hidden only because their folders shrank, which the user can accept as their new size.
        /// </summary>
        [ObservableProperty]
        private bool _hasShrunkGames;

        /// <summary>
        /// The running or last installed games check; lets a caller wait for its result.
        /// </summary>
        public Task InstalledGamesCheck { get; private set; } = Task.CompletedTask;

        public string SelectedTargetsSummary
        {
            get
            {
                var selected = Targets.Count(target => target.IsSelected);
                var shown = Targets.Count(target => target.IsOnDisk);

                return Targets.Count == 0
                    ? "No profiles yet. Add one on the Profiles page."
                    : selected == 0
                        ? $"None of {shown} selected. Back up Vortex games and other games in separate runs."
                        : $"{selected} of {shown} selected";
            }
        }

        [RelayCommand]
        private Task AcceptCurrentSizesAsync() => RunBusyAsync(async () =>
                {
                    var shrunk = Targets.Where(target => target.Presence == GamePresenceState.TooSmall).Select(target => target.Profile).ToList();

                    await _gamePresenceService.AcceptCurrentSizesAsync(shrunk, _storagePathsProvider.GetPaths().GameSizesFilePath);
                    await CheckInstalledGamesAsync();
                });

        /// <summary>
        /// Hides the games whose folders are gone right away, then the ones the size check rejects once it has measured
        /// them (which does not keep the page busy). Hidden games are deselected, so a backup can never include one.
        /// </summary>
        private void StartInstalledGamesCheck()
        {
            if (_settings is not { ShowOnlyInstalledGames: true })
            {
                _installedCheckVersion++;
                ApplyPresence(_ => GamePresenceState.OnDisk);
                return;
            }

            ApplyPresence(target => _gamePresenceService.FoldersExist(target.Profile) ? GamePresenceState.OnDisk : GamePresenceState.Missing);
            InstalledGamesCheck = CheckInstalledGamesAsync();
        }

        private async Task CheckInstalledGamesAsync()
        {
            var version = ++_installedCheckVersion;

            try
            {
                var results = await _gamePresenceService.CheckAsync(_allProfiles, _storagePathsProvider.GetPaths().GameSizesFilePath);

                // Settings or profiles were loaded again meanwhile; a newer check decides.
                if (version != _installedCheckVersion)
                    return;

                var states = results.ToDictionary(result => result.ProfileId, result => result.State);

                ApplyPresence(target => states.GetValueOrDefault(target.Profile.Id, GamePresenceState.OnDisk));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                ReportWarning($"The sizes of installed games could not be checked: {exception.Message}");
            }
        }

        private void ApplyPresence(Func<BackupTargetItemViewModel, GamePresenceState> getState)
        {
            foreach (var target in Targets)
            {
                target.Presence = getState(target);

                if (!target.IsOnDisk)
                    target.IsSelected = false;
            }

            var missing = Targets.Where(target => target.Presence == GamePresenceState.Missing).Select(target => target.GameName).ToList();
            var shrunk = Targets.Where(target => target.Presence == GamePresenceState.TooSmall).Select(target => target.GameName).ToList();
            var parts = new List<string>();

            if (missing.Count > 0)
                parts.Add($"Hidden, not found on this PC: {string.Join(", ", missing)}.");

            if (shrunk.Count > 0)
                parts.Add($"Hidden, less than half the size they had: {string.Join(", ", shrunk)}.");

            HiddenGamesSummary = string.Join(" ", parts);
            HasShrunkGames = shrunk.Count > 0;

            RefreshVisibleTargets();
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
            // Selecting in one section locks the other one; clearing the last selection unlocks both.
            var vortexSelected = VortexTargets.HasSelection;
            var otherSelected = OtherTargets.HasSelection;

            VortexTargets.SetLocked(otherSelected && !vortexSelected);
            OtherTargets.SetLocked(vortexSelected && !otherSelected);
            VortexTargets.NotifySelectionChanged();
            OtherTargets.NotifySelectionChanged();

            OnPropertyChanged(nameof(SelectedTargetsSummary));
            OnPropertyChanged(nameof(ShowVortexComponents));
        }

        /// <summary>
        /// The Vortex components (staging, Vortex AppData) are offered while no game is selected or when Vortex games are
        /// selected. With other games selected they are hidden and not backed up.
        /// </summary>
        public bool ShowVortexComponents => !OtherTargets.HasSelection || VortexTargets.HasSelection;

        private void RefreshVisibleTargets()
        {
            var filter = TargetFilter.Trim();

            VortexTargets.Refresh(filter);
            OtherTargets.Refresh(filter);

            OnPropertyChanged(nameof(HasNoVisibleTargets));
            NotifySelectionChanged();
        }

        public bool HasNoProfiles => Targets.Count == 0;

        public bool HasPausedJob => PausedJob is not null;

        public string PausedJobMessage => PausedJob is null
            ? string.Empty
            : $"A previous backup ({Path.GetFileName(PausedJob.ArchivePath)}) is incomplete. Files that were in use: {PausedJob.FailedFiles.Count}. Close the game and Vortex, then resume.";

        [ObservableProperty]
        private bool _includeStaging;

        [ObservableProperty]
        private bool _includeGameConfigs;

        [ObservableProperty]
        private bool _includeGameAdditions;

        [ObservableProperty]
        private bool _includeGameChanges;

        [ObservableProperty]
        private bool _includeVortexAppData;

        [ObservableProperty]
        private bool _includeCustomFolders;

        [ObservableProperty]
        private bool _includeFullGame;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasPausedJob))]
        [NotifyPropertyChangedFor(nameof(PausedJobMessage))]
        private ActiveBackupJob? _pausedJob;

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
                    VortexTargets.Targets.Clear();
                    OtherTargets.Targets.Clear();

                    foreach (var profile in _allProfiles.OrderBy(profile => profile.GameName, StringComparer.CurrentCultureIgnoreCase))
                    {
                        var target = new BackupTargetItemViewModel(profile, previouslySelected.Contains(profile.Id));
                        target.PropertyChanged += OnTargetPropertyChanged;
                        Targets.Add(target);
                        (profile.UsesVortex ? VortexTargets : OtherTargets).Targets.Add(target);
                    }

                    // A profile switched between Vortex and not Vortex since the last load could leave a mixed selection.
                    if (VortexTargets.HasSelection && OtherTargets.HasSelection)
                    {
                        foreach (var target in OtherTargets.Targets)
                            target.IsSelected = false;
                    }

                    OnPropertyChanged(nameof(HasNoProfiles));
                    StartInstalledGamesCheck();

                    var job = await _backupJobStateService.LoadAsync(paths.ActiveBackupJobFilePath);

                    PausedJob = job is { IsCompleted: false } ? job : null;
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
                IncludeGameChanges: IncludeGameChanges,
                IncludeGameAdditions: IncludeGameAdditions,
                IncludeFullGame: IncludeFullGame);

            if (selected.Count == 0)
            {
                ReportError("Backup: select at least one profile.");
                return;
            }

            if (selected.Any(profile => profile.UsesVortex) && selected.Any(profile => !profile.UsesVortex))
            {
                ReportError("Backup: back up games managed by Vortex and other games in separate runs. Clear the selection of one group first.");
                return;
            }

            if (!options.HasAnyComponent)
            {
                ReportError("Backup: select at least one component to back up.");
                return;
            }

            if (_settings is null || (options.UsesBackupDestination && string.IsNullOrWhiteSpace(_settings.GoogleDriveBackupPath)))
            {
                ReportError("Backup: the backup destination folder is not configured. Set it on the Settings page.");
                return;
            }

            if (options.IncludeVortexAppData && _vortexProcessService.IsVortexRunning()
                && !_confirmationService.Confirm(
                    "Vortex is running",
                    "Vortex is running. It keeps its database in the AppData folder open and keeps writing to it, so the Vortex AppData backup may be inconsistent or miss locked files. Close Vortex first for a reliable backup.\n\nBack up anyway?"))
                return;

            // Asked before the paused job is discarded, so saying no here changes nothing.
            if (options.IncludeFullGame && !await ConfirmDeletingFullGameArchivesAsync(selected))
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

        /// <summary>
        /// Lists the old archives once, before the run. The destination is often a cloud drive, so this stays off the UI thread.
        /// </summary>
        private async Task<bool> ConfirmDeletingFullGameArchivesAsync(IReadOnlyList<BackupProfile> selected)
        {
            var existing = await Task.Run(() => _backupJobRunner.FindExistingFullGameArchives(selected));

            if (existing.Count == 0)
                return true;

            var list = string.Join("\n", existing.Select(archive => $"{archive.ArchivePath} ({FormatSize(archive.Length)})"));

            return _confirmationService.Confirm(
                "Delete old full game archives",
                $"These archives will be deleted before the new ones are written, not overwritten, so a cloud drive does not download them first:\n\n{list}\n\nIf the backup fails or is cancelled, that game has no archive until you run it again. A deleted file on Google Drive goes to its trash and normally counts against your storage until it is emptied.\n\nContinue?");
        }

        private static string FormatSize(long bytes)
        {
            string[] units = ["B", "KB", "MB", "GB", "TB"];
            double value = bytes;
            var unit = 0;

            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            return unit == 0 ? $"{bytes:N0} B" : $"{value:0.0} {units[unit]}";
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

        private void ReportBackupResult(BackupBatchResult batch, AppSettings settings)
        {
            foreach (var result in batch.Results)
            {
                ReportInfo(result.FullGameArchivePath is null
                    ? $"{result.GameName}: {result.FilesArchived:N0} file(s) written, snapshot {result.BackupDirectory}"
                    : $"{result.GameName}: {result.FilesArchived:N0} file(s) written, full game archive {result.FullGameArchivePath}");

                if (result.IgnoredFiles > 0)
                    ReportInfo($"{result.GameName}: game folder ignore list skipped {result.IgnoredFiles:N0} file(s), e.g. {string.Join(", ", result.IgnoredExamples)}");
            }

            foreach (var warning in batch.Results.SelectMany(result => result.Warnings).Concat(batch.Warnings))
                ReportWarning(warning);

            if (batch.Results.All(result => result.FullGameArchivePath is not null))
                ReportSuccess($"Backup finished: {batch.FilesArchived:N0} file(s) written to the full game archive(s).");
            else
                ReportSuccess($"Backup finished: {batch.ModsArchived:N0} changed/new mod(s) archived, {batch.ModsReused:N0} unchanged mod archive(s) reused, {batch.ModsRemoved:N0} removed mod(s) omitted from the new snapshot; {batch.FilesArchived:N0} file(s) written to {settings.GoogleDriveBackupPath}.");

            if (batch.FailedFiles.Count > 0)
                ReportFailedFiles(batch.FailedFiles.Select(file => file.SourcePath).ToList(), "file(s) were in use and are missing from the archives. Close the game or Vortex, then select Continue Backup on the Backup page");
        }

        private static string DescribeComponents(BackupOptions options)
        {
            var names = new List<string>();

            if (options.IncludeStaging)
                names.Add("staging");

            if (options.IncludeGameConfigs)
                names.Add("game configs");

            if (options.IncludeGameAdditions)
                names.Add("new game files");

            if (options.IncludeGameChanges)
                names.Add("changed game files");

            if (options.IncludeVortexAppData)
                names.Add("Vortex AppData");

            if (options.IncludeCustomFolders)
                names.Add("custom paths");

            if (options.IncludeFullGame)
                names.Add("full game");

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
    }
}