using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using UniversalBackupTool.Interface.Interfaces.Appearance;
using UniversalBackupTool.Interface.Interfaces.Backups;
using UniversalBackupTool.Interface.Interfaces.Dialogs;
using UniversalBackupTool.Interface.Interfaces.Jobs;
using UniversalBackupTool.Interface.Interfaces.Operations;
using UniversalBackupTool.Interface.Interfaces.Profiles;
using UniversalBackupTool.Interface.Interfaces.Settings;
using UniversalBackupTool.Interfaces.Storage;
using UniversalBackupTool.Model.Models.Backups;
using UniversalBackupTool.Model.Models.Jobs;
using UniversalBackupTool.Model.Models.Operations;
using UniversalBackupTool.Model.Models.Settings;
using UniversalBackupTool.ViewModel.ViewModels.Base;

namespace UniversalBackupTool.ViewModel.ViewModels.Main
{
    public sealed partial class MainViewModel : BasicViewModel
    {
        private readonly ISettingsStore _settingsStore;
        private readonly IAppStoragePathsProvider _storagePathsProvider;
        private readonly ISafetyBackupService _safetyBackupService;
        private readonly IBackupJobStateService _backupJobStateService;
        private readonly IConfirmationService _confirmationService;
        private readonly IThemeService _themeService;
        private readonly IProfileStore _profileStore;
        private readonly IGamePresenceService _gamePresenceService;

        private bool _closeRequested;

        public MainViewModel(
            ISettingsStore settingsStore,
            IAppStoragePathsProvider storagePathsProvider,
            ISafetyBackupService safetyBackupService,
            IBackupJobStateService backupJobStateService,
            IConfirmationService confirmationService,
            IThemeService themeService,
            IProfileStore profileStore,
            IGamePresenceService gamePresenceService,
            IOperationCoordinator operationCoordinator,
            IOperationOutputService operationOutputService)
            : base(operationOutputService, operationCoordinator)
        {
            _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
            _storagePathsProvider = storagePathsProvider ?? throw new ArgumentNullException(nameof(storagePathsProvider));
            _safetyBackupService = safetyBackupService ?? throw new ArgumentNullException(nameof(safetyBackupService));
            _backupJobStateService = backupJobStateService ?? throw new ArgumentNullException(nameof(backupJobStateService));
            _confirmationService = confirmationService ?? throw new ArgumentNullException(nameof(confirmationService));
            _themeService = themeService ?? throw new ArgumentNullException(nameof(themeService));
            _profileStore = profileStore ?? throw new ArgumentNullException(nameof(profileStore));
            _gamePresenceService = gamePresenceService ?? throw new ArgumentNullException(nameof(gamePresenceService));

            OutputEntries = Output.Entries;
            Coordinator.StateChanged += (_, _) => UpdateOperationState();
        }

        /// <summary>
        /// Raised when the window should close, after a running operation finished cancelling.
        /// </summary>
        public event EventHandler? CloseRequested;

        [ObservableProperty]
        private string _applicationTitle = "Vortex Backup Tool";

        [ObservableProperty]
        private AppSettings? _settings;

        [ObservableProperty]
        private ActiveBackupJob? _pausedJob;

        [ObservableProperty]
        private bool _isOperationRunning;

        [ObservableProperty]
        private string? _operationTitle;

        [ObservableProperty]
        private double _progressPercent;

        [ObservableProperty]
        private bool _isProgressIndeterminate;

        [ObservableProperty]
        private string? _progressText;

        [ObservableProperty]
        private string? _currentItemText;

        [ObservableProperty]
        private bool _isCancelling;

        /// <summary>
        /// Whether the Output tool window is shown. It opens automatically when an operation starts.
        /// </summary>
        [ObservableProperty]
        private bool _isOutputVisible = true;

        /// <summary>
        /// Left part of the status bar: "Ready" or the running operation and its current phase.
        /// </summary>
        [ObservableProperty]
        private string _statusText = "Ready";

        [ObservableProperty]
        private string? _progressPercentText;

        /// <summary>
        /// Files and bytes done of the running phase ("93 / 2,337 files · 230.9 MB / 1.2 GB"). Shown next to the progress
        /// bar, apart from <see cref="StatusText"/>, so a long phase text can never push it out of view.
        /// </summary>
        [ObservableProperty]
        private string? _progressCountsText;

        /// <summary>
        /// Full status with counters and the current file; the status bar tooltip, as the phase text may be cut off.
        /// </summary>
        [ObservableProperty]
        private string? _statusToolTip;

        public ReadOnlyObservableCollection<OutputEntry> OutputEntries { get; }

        public string LogsDirectory => _storagePathsProvider.GetPaths().LogsDirectory;

        [RelayCommand]
        private Task InitializeAsync() => RunBusyAsync(async () =>
                                {
                                    var paths = _storagePathsProvider.GetPaths();

                                    Settings = await _settingsStore.LoadAsync(paths.SettingsFilePath);

                                    _themeService.Apply(Settings.Theme);

                                    await _safetyBackupService.CleanupExpiredAsync(Settings);

                                    var job = await _backupJobStateService.LoadAsync(paths.ActiveBackupJobFilePath);

                                    PausedJob = job is { IsCompleted: false } ? job : null;

                                    var version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
                                    ReportInfo($"Vortex Backup Tool {version?.ToString(3)} started. Application data: {paths.BaseDirectory}");

                                    if (PausedJob is not null)
                                        ReportWarning($"A previous backup is incomplete ({PausedJob.FailedFiles.Count:N0} file(s) were in use). Open Backup & Restore and select Continue Backup.");

                                    if (Settings.ShowOnlyInstalledGames)
                                        _ = MeasureInstalledGamesAsync();
                                });

        /// <summary>
        /// Measures the game folders in the background at startup, so the Backup page already has the result when opened.
        /// </summary>
        private async Task MeasureInstalledGamesAsync()
        {
            try
            {
                var paths = _storagePathsProvider.GetPaths();
                var profiles = await _profileStore.LoadAsync(paths.ProfilesFilePath);

                await _gamePresenceService.CheckAsync(profiles, paths.GameSizesFilePath);
            }
            catch (Exception exception)
            {
                ReportWarning($"The sizes of installed games could not be checked: {exception.Message}");
            }
        }

        [RelayCommand]
        private void ClearOutput() => Output.Clear();

        [RelayCommand]
        private void CancelOperation() => Coordinator.CancelCurrent();

        [RelayCommand]
        private void HideOutput() => IsOutputVisible = false;

        [RelayCommand]
        private void ToggleOutput() => IsOutputVisible = !IsOutputVisible;

        /// <summary>
        /// Called when the window is closing. Returns true when it may close now. While an operation is running the user is
        /// asked to cancel it; the window then closes through <see cref="CloseRequested"/> once the operation has stopped.
        /// </summary>
        public bool TryPrepareClose()
        {
            if (!Coordinator.IsOperationRunning)
                return true;

            if (_closeRequested)
                return false;

            var operationName = Coordinator.CurrentOperationName;

            if (!_confirmationService.Confirm(
                    "Operation in progress",
                    $"\"{operationName}\" is still running.\n\nCancel it and exit when it has stopped?"))
            {
                return false;
            }

            _closeRequested = true;
            ReportWarning($"Exit requested: waiting for \"{operationName}\" to stop.");
            Coordinator.CancelCurrent();

            return false;
        }

        private void UpdateOperationState()
        {
            var wasRunning = IsOperationRunning;

            IsOperationRunning = Coordinator.IsOperationRunning;
            OperationTitle = Coordinator.CurrentOperationName;
            IsCancelling = Coordinator.IsCancellationRequested;

            var progress = Coordinator.CurrentProgress;

            if (!IsOperationRunning)
            {
                ProgressPercent = 0;
                IsProgressIndeterminate = false;
                ProgressText = null;
                ProgressPercentText = null;
                ProgressCountsText = null;
                CurrentItemText = null;
                StatusText = "Ready";
                StatusToolTip = null;

                if (_closeRequested)
                    CloseRequested?.Invoke(this, EventArgs.Empty);

                return;
            }

            if (!wasRunning)
                IsOutputVisible = true;

            IsProgressIndeterminate = progress is null || (progress.TotalBytes <= 0 && progress.TotalFiles <= 0);
            ProgressPercent = progress is null ? 0 : progress.Fraction * 100;
            ProgressPercentText = IsProgressIndeterminate ? null : $"{ProgressPercent:0}%";
            ProgressCountsText = progress is null ? null : FormatCounts(progress);
            ProgressText = progress is null
                ? "Starting..."
                : ProgressCountsText is null ? progress.Phase : $"{progress.Phase}: {ProgressCountsText}";
            CurrentItemText = progress?.CurrentItem;
            StatusText = IsCancelling
                ? $"{OperationTitle}: cancelling..."
                : $"{OperationTitle}  ·  {progress?.Phase ?? "Starting..."}";
            StatusToolTip = CurrentItemText is null
                ? $"{OperationTitle}  ·  {ProgressText}"
                : $"{OperationTitle}  ·  {ProgressText}{Environment.NewLine}{CurrentItemText}";
        }

        private static string? FormatCounts(BackupProgress progress)
        {
            if (progress.TotalBytes <= 0 && progress.TotalFiles <= 0)
                return null;

            var parts = new List<string>();

            if (progress.TotalFiles > 0)
                parts.Add($"{progress.FilesProcessed:N0} / {progress.TotalFiles:N0} files");

            if (progress.TotalBytes > 0)
                parts.Add($"{FormatBytes(progress.BytesProcessed)} / {FormatBytes(progress.TotalBytes)}");

            return string.Join("  ·  ", parts);
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