using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using UniversalBackupTool.Interface.Interfaces.Dialogs;
using UniversalBackupTool.Interface.Interfaces.Profiles;
using UniversalBackupTool.Interfaces.Snapshots;
using UniversalBackupTool.Interfaces.Storage;
using UniversalBackupTool.Model.Models.Snapshots;
using UniversalBackupTool.ViewModel.ViewModels.Base;
using UniversalBackupTool.ViewModel.ViewModels.Dashboard;

namespace UniversalBackupTool.ViewModel.ViewModels.VanillaPurge
{
    public sealed partial class VanillaPurgeViewModel : BasicViewModel
    {
        private const int MaxPreviewItems = 500;

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
            IConfirmationService confirmationService)
        {
            _profileStore = profileStore ?? throw new ArgumentNullException(nameof(profileStore));
            _snapshotService = snapshotService ?? throw new ArgumentNullException(nameof(snapshotService));
            _snapshotStore = snapshotStore ?? throw new ArgumentNullException(nameof(snapshotStore));
            _storagePathsProvider = storagePathsProvider ?? throw new ArgumentNullException(nameof(storagePathsProvider));
            _confirmationService = confirmationService ?? throw new ArgumentNullException(nameof(confirmationService));
        }

        public ObservableCollection<ProfileItemViewModel> Profiles { get; } = [];

        public ObservableCollection<string> PreviewFiles { get; } = [];

        public bool HasNoProfiles => Profiles.Count == 0;

        public bool HasPreview => PreviewFiles.Count > 0;

        public bool HasPreviewSummary => !string.IsNullOrEmpty(PreviewSummary);

        public bool HasSnapshot => _currentSnapshot is not null;

        public bool HasProfileSelected => SelectedProfile is not null;

        public bool HasStatusMessage => !string.IsNullOrEmpty(StatusMessage);

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasProfileSelected))]
        [NotifyPropertyChangedFor(nameof(GameRootPath))]
        private ProfileItemViewModel? _selectedProfile;

        [ObservableProperty]
        private string _snapshotStatus = "Select a profile.";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasPreviewSummary))]
        private string? _previewSummary;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
        private string? _statusMessage;

        public string? GameRootPath => SelectedProfile?.Profile.GameRootPath;

        partial void OnSelectedProfileChanged(ProfileItemViewModel? value)
        {
            _ = RefreshSelectionAsync();
        }

        [RelayCommand]
        private Task LoadAsync() => RunBusyAsync(async () =>
                {
                    StatusMessage = null;

                    var previousId = SelectedProfile?.Profile.Id;
                    var profiles = await _profileStore.LoadAsync(_storagePathsProvider.GetPaths().ProfilesFilePath);

                    Profiles.Clear();

                    foreach (var profile in profiles.Where(profile => !string.IsNullOrWhiteSpace(profile.GameRootPath)))
                        Profiles.Add(new ProfileItemViewModel(profile));

                    OnPropertyChanged(nameof(HasNoProfiles));

                    SelectedProfile = Profiles.FirstOrDefault(item => item.Profile.Id == previousId) ?? Profiles.FirstOrDefault();
                });

        [RelayCommand]
        private Task CreateSnapshotAsync() => RunBusyAsync(async () =>
                {
                    StatusMessage = null;

                    var rootPath = GetValidRootPath();

                    if (rootPath is null)
                        return;

                    if (_currentSnapshot is not null &&
                        !_confirmationService.Confirm("Update snapshot", "A baseline snapshot already exists for this profile. Replace it with the current state of the game folder?"))
                    {
                        return;
                    }

                    var profileId = SelectedProfile!.Profile.Id;
                    var snapshot = await Task.Run(() => _snapshotService.CreateSnapshotAsync(profileId, rootPath));

                    if (snapshot.Files.Count == 0)
                    {
                        ErrorMessage = "The game folder contains no files, so no snapshot was saved.";
                        return;
                    }

                    await _snapshotStore.SaveAsync(_storagePathsProvider.GetPaths().SnapshotsDirectory, snapshot);

                    await LoadSnapshotAsync(profileId);
                    ClearPreview();

                    StatusMessage = $"Snapshot saved: {snapshot.Files.Count:N0} files.";
                });

        [RelayCommand]
        private Task PreviewAsync() => RunBusyAsync(async () =>
                {
                    StatusMessage = null;

                    var unregistered = await FindUnregisteredFilesAsync();

                    if (unregistered is null)
                        return;

                    ShowPreview(unregistered);
                });

        [RelayCommand]
        private Task PurgeAsync() => RunBusyAsync(async () =>
                {
                    StatusMessage = null;

                    var rootPath = GetValidRootPath();

                    if (rootPath is null)
                        return;

                    var unregistered = await FindUnregisteredFilesAsync();

                    if (unregistered is null)
                        return;

                    ShowPreview(unregistered);

                    if (unregistered.Count == 0)
                    {
                        StatusMessage = "Nothing to purge: the game folder matches the baseline snapshot.";
                        return;
                    }

                    var confirmed = _confirmationService.Confirm(
                        "Execute Safe Purge",
                        $"{unregistered.Count:N0} file(s) not present in the baseline snapshot will be deleted from:\n{rootPath}\n\nA Safety Backup is created first. Continue?");

                    if (!confirmed)
                        return;

                    var snapshot = _currentSnapshot!.Data;

                    await Task.Run(() => _snapshotService.PurgeUnregisteredFilesAsync(rootPath, snapshot));

                    ClearPreview();

                    StatusMessage = $"Purge completed: {unregistered.Count:N0} file(s) deleted. A Safety Backup was created beforehand.";
                });

        private async Task<List<string>?> FindUnregisteredFilesAsync()
        {
            var rootPath = GetValidRootPath();

            if (rootPath is null)
                return null;

            if (_currentSnapshot is null)
            {
                ErrorMessage = "Create a baseline snapshot first.";
                return null;
            }

            var baseline = _currentSnapshot.Data;
            var changes = await Task.Run(() => _snapshotService.GetChangesAsync(rootPath, baseline));

            // Changes also contain modified and deleted files, which a purge never touches.
            return changes.Keys
                .Where(path => !baseline.Files.ContainsKey(path))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private string? GetValidRootPath()
        {
            var rootPath = GameRootPath;

            if (SelectedProfile is null || string.IsNullOrWhiteSpace(rootPath))
            {
                ErrorMessage = "Select a profile with a game root folder.";
                return null;
            }

            if (!Directory.Exists(rootPath))
            {
                ErrorMessage = $"Game root folder does not exist: {rootPath}";
                return null;
            }

            var fullPath = Path.GetFullPath(rootPath);

            if (string.Equals(fullPath, Path.GetPathRoot(fullPath), StringComparison.OrdinalIgnoreCase))
            {
                ErrorMessage = "A drive root cannot be used as a game folder.";
                return null;
            }

            return rootPath;
        }

        private void ShowPreview(List<string> unregistered)
        {
            PreviewFiles.Clear();

            foreach (var path in unregistered.Take(MaxPreviewItems))
                PreviewFiles.Add(path);

            PreviewSummary = unregistered.Count == 0
                ? "No unregistered files found."
                : unregistered.Count > MaxPreviewItems
                    ? $"{unregistered.Count:N0} unregistered files would be deleted (showing first {MaxPreviewItems})."
                    : $"{unregistered.Count:N0} unregistered files would be deleted.";

            OnPropertyChanged(nameof(HasPreview));
        }

        private void ClearPreview()
        {
            PreviewFiles.Clear();
            PreviewSummary = null;
            OnPropertyChanged(nameof(HasPreview));
        }

        private async Task RefreshSelectionAsync()
        {
            ClearPreview();
            StatusMessage = null;

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
                ErrorMessage = exception.Message;
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
    }
}