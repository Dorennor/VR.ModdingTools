using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using UniversalBackupTool.Interface.Interfaces.Backups;
using UniversalBackupTool.Interface.Interfaces.Jobs;
using UniversalBackupTool.Interface.Interfaces.Settings;
using UniversalBackupTool.Interfaces.Storage;
using UniversalBackupTool.Model.Models.Jobs;
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

        public MainViewModel(
            ISettingsStore settingsStore,
            IAppStoragePathsProvider storagePathsProvider,
            ISafetyBackupService safetyBackupService,
            IBackupJobStateService backupJobStateService)
        {
            _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
            _storagePathsProvider = storagePathsProvider ?? throw new ArgumentNullException(nameof(storagePathsProvider));
            _safetyBackupService = safetyBackupService ?? throw new ArgumentNullException(nameof(safetyBackupService));
            _backupJobStateService = backupJobStateService ?? throw new ArgumentNullException(nameof(backupJobStateService));
        }

        [ObservableProperty]
        private string _applicationTitle = "Vortex Backup Tool";

        [ObservableProperty]
        private AppSettings? _settings;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasPausedJob))]
        [NotifyPropertyChangedFor(nameof(PausedJobMessage))]
        private ActiveBackupJob? _pausedJob;

        public bool HasPausedJob => PausedJob is not null;

        public string PausedJobMessage => PausedJob is null
            ? string.Empty
            : $"A previous backup job ({Path.GetFileName(PausedJob.ArchivePath)}) was not completed. Locked files: {PausedJob.FailedPaths.Count}.";

        [RelayCommand]
        private Task InitializeAsync() => RunBusyAsync(async () =>
                                {
                                    var paths = _storagePathsProvider.GetPaths();

                                    Settings = await _settingsStore.LoadAsync(paths.SettingsFilePath);

                                    await _safetyBackupService.CleanupExpiredAsync(Settings);

                                    var job = await _backupJobStateService.LoadAsync(paths.ActiveBackupJobFilePath);

                                    PausedJob = job is { IsCompleted: false } ? job : null;
                                });
    }
}