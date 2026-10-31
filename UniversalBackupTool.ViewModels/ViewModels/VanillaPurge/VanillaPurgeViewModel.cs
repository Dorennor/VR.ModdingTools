using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using UniversalBackupTool.Interface.Interfaces.Dialogs;
using UniversalBackupTool.Interface.Interfaces.Operations;
using UniversalBackupTool.Interface.Interfaces.Profiles;
using UniversalBackupTool.Interfaces.Snapshots;
using UniversalBackupTool.Interfaces.Storage;
using UniversalBackupTool.Model.Models.Profiles;
using UniversalBackupTool.Model.Models.Snapshots;
using UniversalBackupTool.ViewModel.ViewModels.Base;
using UniversalBackupTool.ViewModel.ViewModels.Dashboard;

namespace UniversalBackupTool.ViewModel.ViewModels.VanillaPurge
{
    public sealed partial class VanillaPurgeViewModel : BasicViewModel
    {
        private readonly IProfileStore _profileStore;
        private readonly ISnapshotService _snapshotService;
        private readonly ISnapshotStore _snapshotStore;
        private readonly IAppStoragePathsProvider _storagePathsProvider;
        private readonly IConfirmationService _confirmationService;

        private StoredSnapshot? _currentSnapshot;

        public VanillaPurgeViewModel(
            IProfileStore profileStore,
            ISnapshotService snapshotService,
            ISnapshotStore snapshotStore,
            IAppStoragePathsProvider storagePathsProvider,
            IConfirmationService confirmationService,
            IOperationCoordinator operationCoordinator,
            IOperationOutputService operationOutputService)
            : base(operationOutputService, operationCoordinator)
        {
            _profileStore = profileStore ?? throw new ArgumentNullException(nameof(profileStore));
            _snapshotService = snapshotService ?? throw new ArgumentNullException(nameof(snapshotService));
            _snapshotStore = snapshotStore ?? throw new ArgumentNullException(nameof(snapshotStore));
            _storagePathsProvider = storagePathsProvider ?? throw new ArgumentNullException(nameof(storagePathsProvider));
            _confirmationService = confirmationService ?? throw new ArgumentNullException(nameof(confirmationService));

            PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(CanStartOperation) or nameof(HasProfileSelected) or nameof(HasSnapshot))
                {
                    OnPropertyChanged(nameof(CanCreateSnapshot));
                    OnPropertyChanged(nameof(CanPurge));
                }
            };
        }

        public ObservableCollection<ProfileItemViewModel> Profiles { get; } = [];

        /// <summary>
        /// All unregistered files. Replaced as a whole (not item by item) so large lists cost one UI update; the view virtualizes it.
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasPreview))]
        private IReadOnlyList<string> _previewFiles = [];

        public bool HasNoProfiles => Profiles.Count == 0;

        public bool HasPreview => PreviewFiles.Count > 0;

        public bool HasPreviewSummary => !string.IsNullOrEmpty(PreviewSummary);

        public bool HasSnapshot => _currentSnapshot is not null;

        public bool HasProfileSelected => SelectedProfile is not null;

        public bool CanCreateSnapshot => HasProfileSelected && CanStartOperation;

        public bool CanPurge => HasSnapshot && CanStartOperation;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasProfileSelected))]
        [NotifyPropertyChangedFor(nameof(GameRootPath))]
        private ProfileItemViewModel? _selectedProfile;

        [ObservableProperty]
        private string _snapshotStatus = "Select a profile.";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasPreviewSummary))]
        private string? _previewSummary;

        public string? GameRootPath => SelectedProfile?.Profile.GameRootPath;

        partial void OnSelectedProfileChanged(ProfileItemViewModel? value)
        {
            _ = RefreshSelectionAsync();
        }

        [RelayCommand]
        private Task LoadAsync() => RunBusyAsync(async () =>
                {
                    var previousId = SelectedProfile?.Profile.Id;
                    var profiles = await _profileStore.LoadAsync(_storagePathsProvider.GetPaths().ProfilesFilePath);

                    Profiles.Clear();

                    foreach (var profile in profiles.Where(profile => !string.IsNullOrWhiteSpace(profile.GameRootPath)))
                        Profiles.Add(new ProfileItemViewModel(profile));

                    OnPropertyChanged(nameof(HasNoProfiles));

                    SelectedProfile = Profiles.FirstOrDefault(item => item.Profile.Id == previousId) ?? Profiles.FirstOrDefault();
                });

        [RelayCommand]
        private async Task CreateSnapshotAsync()
        {
            var rootPath = GetValidRootPath();

            if (rootPath is null)
                return;

            if (_currentSnapshot is not null &&
                !_confirmationService.Confirm("Update snapshot", "A baseline snapshot already exists for this profile. Replace it with the current state of the game folder?"))
            {
                return;
            }

            var profile = SelectedProfile!.Profile;

            await RunOperationAsync("Create Snapshot", async scope =>
            {
                scope.Report(new($"scanning {rootPath}", null, 0, 0, 0, 0));

                var snapshot = await Task.Run(() => _snapshotService.CreateSnapshotAsync(profile.Id, rootPath, scope.CancellationToken), scope.CancellationToken);

                if (snapshot.Files.Count == 0)
                {
                    ReportError($"{profile.GameName}: the game folder contains no files, so no snapshot was saved.");
                    return;
                }

                await _snapshotStore.SaveAsync(_storagePathsProvider.GetPaths().SnapshotsDirectory, snapshot, scope.CancellationToken);

                if (SelectedProfile?.Profile.Id == profile.Id)
                {
                    await LoadSnapshotAsync(profile.Id);
                    ClearPreview();
                }

                ReportSuccess($"{profile.GameName}: baseline snapshot saved, {snapshot.Files.Count:N0} files.");
            });
        }

        [RelayCommand]
        private async Task PreviewAsync()
        {
            var target = GetPurgeTarget();

            if (target is null)
                return;

            await RunOperationAsync("Purge Preview", async scope =>
            {
                var unregistered = await FindUnregisteredFilesAsync(target, scope);
                ShowPreviewIfStillSelected(target, unregistered);
                ReportInfo($"{target.Profile.GameName}: {unregistered.Count:N0} unregistered file(s) would be deleted from {target.RootPath}.");
            });
        }

        [RelayCommand]
        private async Task PurgeAsync()
        {
            var target = GetPurgeTarget();

            if (target is null)
                return;

            await RunOperationAsync("Purge", async scope =>
            {
                var unregistered = await FindUnregisteredFilesAsync(target, scope);
                ShowPreviewIfStillSelected(target, unregistered);

                if (unregistered.Count == 0)
                {
                    ReportSuccess($"{target.Profile.GameName}: nothing to purge, the game folder matches the baseline snapshot.");
                    return;
                }

                var confirmed = _confirmationService.Confirm(
                    "Execute Safe Purge",
                    $"{unregistered.Count:N0} file(s) not present in the baseline snapshot will be deleted from:\n{target.RootPath}\n\nA Safety Backup is created first. Continue?");

                if (!confirmed)
                {
                    ReportInfo("Purge was not confirmed; nothing was deleted.");
                    return;
                }

                scope.Report(new("creating safety backup and deleting files", null, 0, 0, 0, unregistered.Count));

                var result = await Task.Run(() => _snapshotService.PurgeUnregisteredFilesAsync(target.RootPath, target.Snapshot, scope.CancellationToken), scope.CancellationToken);

                if (SelectedProfile?.Profile.Id == target.Profile.Id)
                    ClearPreview();

                if (result.SafetyBackupPath is not null)
                    ReportInfo($"Deleted files were saved to {result.SafetyBackupPath}");

                ReportSuccess($"Purge completed for {target.Profile.GameName}: {result.FilesDeleted:N0} file(s) deleted.");
            });
        }

        private PurgeTarget? GetPurgeTarget()
        {
            var rootPath = GetValidRootPath();

            if (rootPath is null)
                return null;

            if (_currentSnapshot is null)
            {
                ReportError($"{SelectedProfile!.GameName}: create a baseline snapshot first.");
                return null;
            }

            return new PurgeTarget(SelectedProfile!.Profile, rootPath, _currentSnapshot.Data);
        }

        private async Task<List<string>> FindUnregisteredFilesAsync(PurgeTarget target, IOperationScope scope)
        {
            scope.Report(new($"comparing {target.RootPath} with the baseline snapshot", null, 0, 0, 0, 0));

            var changes = await Task.Run(() => _snapshotService.GetChangesAsync(target.RootPath, target.Snapshot, scope.CancellationToken), scope.CancellationToken);

            // Changes also contain modified and deleted files, which a purge never touches.
            return changes.Keys
                .Where(path => !target.Snapshot.Files.ContainsKey(path))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private void ShowPreviewIfStillSelected(PurgeTarget target, List<string> unregistered)
        {
            if (SelectedProfile?.Profile.Id == target.Profile.Id)
                ShowPreview(unregistered);
        }

        private string? GetValidRootPath()
        {
            var rootPath = GameRootPath;

            if (SelectedProfile is null || string.IsNullOrWhiteSpace(rootPath))
            {
                ReportError("Select a profile with a game root folder.");
                return null;
            }

            if (!Directory.Exists(rootPath))
            {
                ReportError($"Game root folder does not exist: {rootPath}");
                return null;
            }

            var fullPath = Path.GetFullPath(rootPath);

            if (string.Equals(fullPath, Path.GetPathRoot(fullPath), StringComparison.OrdinalIgnoreCase))
            {
                ReportError("A drive root cannot be used as a game folder.");
                return null;
            }

            return rootPath;
        }

        private void ShowPreview(List<string> unregistered)
        {
            PreviewFiles = unregistered;

            PreviewSummary = unregistered.Count == 0
                ? "No unregistered files found."
                : $"{unregistered.Count:N0} unregistered files would be deleted.";
        }

        private void ClearPreview()
        {
            PreviewFiles = [];
            PreviewSummary = null;
        }

        private async Task RefreshSelectionAsync()
        {
            ClearPreview();

            try
            {
                if (SelectedProfile is null)
                {
                    _currentSnapshot = null;
                    UpdateSnapshotStatus();
                    return;
                }

                await LoadSnapshotAsync(SelectedProfile.Profile.Id);
            }
            catch (Exception exception)
            {
                ReportError(exception.Message);
            }
        }

        private async Task LoadSnapshotAsync(Guid profileId)
        {
            _currentSnapshot = await _snapshotStore.LoadAsync(_storagePathsProvider.GetPaths().SnapshotsDirectory, profileId);

            UpdateSnapshotStatus();
        }

        private void UpdateSnapshotStatus()
        {
            SnapshotStatus = SelectedProfile is null
                ? "Select a profile."
                : _currentSnapshot is null
                    ? "No baseline snapshot yet. Create one while the game folder is in a clean, vanilla state."
                    : $"Created {_currentSnapshot.CreatedAt:g}. Contains {_currentSnapshot.Data.Files.Count:N0} files.";

            OnPropertyChanged(nameof(HasSnapshot));
        }

        private sealed record PurgeTarget(BackupProfile Profile, string RootPath, SnapshotData Snapshot);
    }
}
