using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using UniversalBackupTool.Interface.Interfaces.Dialogs;
using UniversalBackupTool.Interface.Interfaces.Paths;
using UniversalBackupTool.Interface.Interfaces.Profiles;
using UniversalBackupTool.Interfaces.Storage;
using UniversalBackupTool.Model.Models.Profiles;
using UniversalBackupTool.ViewModel.ViewModels.Base;

namespace UniversalBackupTool.ViewModel.ViewModels.Dashboard
{
    public sealed partial class DashboardViewModel : BasicViewModel
    {
        private readonly IProfileStore _profileStore;
        private readonly IPathDriveService _pathDriveService;
        private readonly ISystemPathsProvider _systemPathsProvider;
        private readonly IAppStoragePathsProvider _storagePathsProvider;
        private readonly IFolderPickerService _folderPickerService;

        private Guid? _editingProfileId;
        private BackupProfile? _editingOriginal;

        public DashboardViewModel(
            IProfileStore profileStore,
            IPathDriveService pathDriveService,
            ISystemPathsProvider systemPathsProvider,
            IAppStoragePathsProvider storagePathsProvider,
            IFolderPickerService folderPickerService)
        {
            _profileStore = profileStore ?? throw new ArgumentNullException(nameof(profileStore));
            _pathDriveService = pathDriveService ?? throw new ArgumentNullException(nameof(pathDriveService));
            _systemPathsProvider = systemPathsProvider ?? throw new ArgumentNullException(nameof(systemPathsProvider));
            _storagePathsProvider = storagePathsProvider ?? throw new ArgumentNullException(nameof(storagePathsProvider));
            _folderPickerService = folderPickerService ?? throw new ArgumentNullException(nameof(folderPickerService));

            AvailableDrives = _systemPathsProvider.GetAvailableDrives();
        }

        public ObservableCollection<ProfileItemViewModel> Profiles { get; } = [];

        public IReadOnlyList<string> AvailableDrives { get; }

        public bool HasNoProfiles => Profiles.Count == 0;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(EditorTitle))]
        private bool _isEditing;

        [ObservableProperty]
        private string _editGameName = string.Empty;

        [ObservableProperty]
        private string _editVortexStagingPath = string.Empty;

        [ObservableProperty]
        private string _editGameRootPath = string.Empty;

        [ObservableProperty]
        private string _editVortexAppDataPath = string.Empty;

        [ObservableProperty]
        private string _editCustomFoldersText = string.Empty;

        [ObservableProperty]
        private bool _editUseIgnoreList = true;

        [ObservableProperty]
        private bool _isFindVisible;

        [ObservableProperty]
        private ProfilePathEntry? _selectedFindPath;

        [ObservableProperty]
        private string? _selectedCandidate;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasFindMessage))]
        private string? _findMessage;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
        private string? _statusMessage;

        public ObservableCollection<ProfilePathEntry> FindablePaths { get; } = [];

        public ObservableCollection<string> FoundCandidates { get; } = [];

        public bool HasFindMessage => !string.IsNullOrEmpty(FindMessage);

        public bool HasCandidates => FoundCandidates.Count > 0;

        public bool HasStatusMessage => !string.IsNullOrEmpty(StatusMessage);

        public string EditorTitle => _editingProfileId is null ? "New Profile" : "Edit Profile";

        public string? EditVortexStagingDrive
        {
            get => _pathDriveService.GetDrive(EditVortexStagingPath);
            set => SwapDrive(value, EditVortexStagingPath, path => EditVortexStagingPath = path, nameof(EditVortexStagingDrive));
        }

        public string? EditGameRootDrive
        {
            get => _pathDriveService.GetDrive(EditGameRootPath);
            set => SwapDrive(value, EditGameRootPath, path => EditGameRootPath = path, nameof(EditGameRootDrive));
        }

        public string? EditVortexAppDataDrive
        {
            get => _pathDriveService.GetDrive(EditVortexAppDataPath);
            set => SwapDrive(value, EditVortexAppDataPath, path => EditVortexAppDataPath = path, nameof(EditVortexAppDataDrive));
        }

        private string ProfilesFilePath => _storagePathsProvider.GetPaths().ProfilesFilePath;

        private partial void OnEditVortexStagingPathChanged(string value) => OnPropertyChanged(nameof(EditVortexStagingDrive));

        private partial void OnEditGameRootPathChanged(string value) => OnPropertyChanged(nameof(EditGameRootDrive));

        private partial void OnEditVortexAppDataPathChanged(string value) => OnPropertyChanged(nameof(EditVortexAppDataDrive));

        private partial void OnSelectedFindPathChanged(ProfilePathEntry? value) => ClearFindResults();

        [RelayCommand]
        private Task LoadAsync() => RunBusyAsync(async () =>
                {
                    StatusMessage = null;

                    var profiles = await _profileStore.LoadAsync(ProfilesFilePath);

                    ReplaceProfiles(profiles);
                });

        [RelayCommand]
        private void AddProfile()
        {
            ResetMessages();

            _editingProfileId = null;
            _editingOriginal = null;

            EditGameName = string.Empty;
            EditVortexStagingPath = string.Empty;
            EditGameRootPath = string.Empty;
            EditVortexAppDataPath = _systemPathsProvider.GetDefaultVortexAppDataPath();
            EditCustomFoldersText = string.Empty;
            EditUseIgnoreList = true;

            IsFindVisible = false;
            IsEditing = true;
            OnPropertyChanged(nameof(EditorTitle));
        }

        [RelayCommand]
        private void EditProfile(ProfileItemViewModel? item)
        {
            if (item is null)
                return;

            ResetMessages();

            var profile = item.Profile;

            _editingProfileId = profile.Id;
            _editingOriginal = profile;

            EditGameName = profile.GameName;
            EditVortexStagingPath = profile.VortexStagingPath ?? string.Empty;
            EditGameRootPath = profile.GameRootPath ?? string.Empty;
            EditVortexAppDataPath = profile.VortexAppDataPath ?? string.Empty;
            EditCustomFoldersText = string.Join(Environment.NewLine, profile.CustomFolders);
            EditUseIgnoreList = profile.UseIgnoreList;

            IsFindVisible = false;
            IsEditing = true;
            OnPropertyChanged(nameof(EditorTitle));
        }

        [RelayCommand]
        private void CancelEdit()
        {
            ErrorMessage = null;
            IsEditing = false;
        }

        [RelayCommand]
        private Task SaveProfileAsync() => RunBusyAsync(async () =>
                {
                    StatusMessage = null;

                    var customFolders = ParseLines(EditCustomFoldersText);
                    var validationError = ValidateEditor(customFolders);

                    if (validationError is not null)
                    {
                        ErrorMessage = validationError;
                        return;
                    }

                    var edited = (_editingOriginal ?? new BackupProfile()) with
                    {
                        GameName = EditGameName.Trim(),
                        VortexStagingPath = NullIfEmpty(EditVortexStagingPath),
                        GameRootPath = NullIfEmpty(EditGameRootPath),
                        VortexAppDataPath = NullIfEmpty(EditVortexAppDataPath),
                        CustomFolders = customFolders,
                        UseIgnoreList = EditUseIgnoreList
                    };

                    var updated = Profiles.Select(item => item.Profile).ToList();
                    var existingIndex = updated.FindIndex(profile => profile.Id == edited.Id);

                    if (existingIndex >= 0)
                        updated[existingIndex] = edited;
                    else
                        updated.Add(edited);

                    await _profileStore.SaveAsync(ProfilesFilePath, updated);

                    ReplaceProfiles(updated);

                    IsEditing = false;
                    StatusMessage = $"Profile \"{edited.GameName}\" saved.";
                });

        [RelayCommand]
        private Task DeleteProfileAsync(ProfileItemViewModel? item) => RunBusyAsync(async () =>
                {
                    if (item is null)
                        return;

                    StatusMessage = null;

                    var updated = Profiles
                        .Select(profileItem => profileItem.Profile)
                        .Where(profile => profile.Id != item.Profile.Id)
                        .ToList();

                    await _profileStore.SaveAsync(ProfilesFilePath, updated);

                    ReplaceProfiles(updated);

                    if (_editingProfileId == item.Profile.Id)
                        IsEditing = false;

                    StatusMessage = $"Profile \"{item.GameName}\" deleted.";
                });

        [RelayCommand]
        private void BrowseVortexStagingPath() => PickInto(EditVortexStagingPath, path => EditVortexStagingPath = path);

        [RelayCommand]
        private void BrowseGameRootPath() => PickInto(EditGameRootPath, path => EditGameRootPath = path);

        [RelayCommand]
        private void BrowseVortexAppDataPath() => PickInto(EditVortexAppDataPath, path => EditVortexAppDataPath = path);

        [RelayCommand]
        private void AddCustomFolder()
        {
            PickInto(null, path =>
            {
                var existing = ParseLines(EditCustomFoldersText);

                if (!existing.Contains(path, StringComparer.OrdinalIgnoreCase))
                    existing.Add(path);

                EditCustomFoldersText = string.Join(Environment.NewLine, existing);
            });
        }

        [RelayCommand]
        private void ToggleFind()
        {
            ResetMessages();

            IsFindVisible = !IsFindVisible;

            if (!IsFindVisible)
                return;

            IsEditing = false;
            RebuildFindablePaths();
        }

        [RelayCommand]
        private Task FindOnOtherDisksAsync() => RunBusyAsync(async () =>
                {
                    StatusMessage = null;
                    ClearFindResults();

                    if (SelectedFindPath is null)
                    {
                        ErrorMessage = "Select a path to look for.";
                        return;
                    }

                    var path = SelectedFindPath.Path;
                    var found = await Task.Run(() => _pathDriveService.FindOnOtherDrives(path));

                    foreach (var candidate in found)
                        FoundCandidates.Add(candidate);

                    OnPropertyChanged(nameof(HasCandidates));

                    SelectedCandidate = FoundCandidates.FirstOrDefault();
                    FindMessage = found.Count == 0
                        ? "This path was not found on any other disk."
                        : $"Found on {found.Count} other disk(s). Choose the new path and save it.";
                });

        [RelayCommand]
        private Task ApplyCandidateAsync() => RunBusyAsync(async () =>
                {
                    StatusMessage = null;

                    if (SelectedFindPath is null || string.IsNullOrWhiteSpace(SelectedCandidate))
                    {
                        ErrorMessage = "Select the found path to save.";
                        return;
                    }

                    var entry = SelectedFindPath;
                    var candidate = SelectedCandidate;

                    var updated = Profiles
                        .Select(item => item.Profile.Id == entry.ProfileId ? WithPath(item.Profile, entry, candidate) : item.Profile)
                        .ToList();

                    await _profileStore.SaveAsync(ProfilesFilePath, updated);

                    ReplaceProfiles(updated);

                    IsFindVisible = false;
                    StatusMessage = $"Path for \"{entry.GameName}\" ({entry.KindLabel}) changed to {candidate}.";
                });

        private static BackupProfile WithPath(BackupProfile profile, ProfilePathEntry entry, string newPath)
        {
            return entry.Kind switch
            {
                ProfilePathKind.VortexStaging => profile with { VortexStagingPath = newPath },
                ProfilePathKind.GameRoot => profile with { GameRootPath = newPath },
                ProfilePathKind.VortexAppData => profile with { VortexAppDataPath = newPath },
                _ => profile with
                {
                    CustomFolders = profile.CustomFolders
                        .Select(folder => string.Equals(folder, entry.Path, StringComparison.Ordinal) ? newPath : folder)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList()
                }
            };
        }

        private void RebuildFindablePaths()
        {
            FindablePaths.Clear();
            ClearFindResults();

            foreach (var profile in Profiles.Select(item => item.Profile))
            {
                AddFindable(profile, ProfilePathKind.VortexStaging, profile.VortexStagingPath);
                AddFindable(profile, ProfilePathKind.GameRoot, profile.GameRootPath);
                AddFindable(profile, ProfilePathKind.VortexAppData, profile.VortexAppDataPath);

                foreach (var folder in profile.CustomFolders)
                    AddFindable(profile, ProfilePathKind.CustomFolder, folder);
            }

            SelectedFindPath = FindablePaths.FirstOrDefault();

            if (FindablePaths.Count == 0)
                FindMessage = "No profile has a path on a drive yet.";
        }

        private void AddFindable(BackupProfile profile, ProfilePathKind kind, string? path)
        {
            if (_pathDriveService.GetDrive(path) is not null)
                FindablePaths.Add(new ProfilePathEntry(profile.Id, profile.GameName, kind, path!));
        }

        private void ClearFindResults()
        {
            FoundCandidates.Clear();
            SelectedCandidate = null;
            FindMessage = null;
            OnPropertyChanged(nameof(HasCandidates));
        }

        private void SwapDrive(string? newDrive, string currentPath, Action<string> apply, string propertyName)
        {
            if (string.IsNullOrWhiteSpace(newDrive))
                return;

            try
            {
                apply(_pathDriveService.ReplaceDrive(currentPath, newDrive));
            }
            catch (ArgumentException exception)
            {
                ErrorMessage = exception.Message;
                OnPropertyChanged(propertyName);
            }
        }

        private void ResetMessages()
        {
            StatusMessage = null;
            ErrorMessage = null;
        }

        private void ReplaceProfiles(IEnumerable<BackupProfile> profiles)
        {
            Profiles.Clear();

            foreach (var profile in profiles)
                Profiles.Add(new ProfileItemViewModel(profile));

            OnPropertyChanged(nameof(HasNoProfiles));
        }

        private void PickInto(string? initialPath, Action<string> apply)
        {
            var selected = _folderPickerService.PickFolder(initialPath);

            if (!string.IsNullOrWhiteSpace(selected))
                apply(selected);
        }

        private string? ValidateEditor(List<string> customFolders)
        {
            var name = EditGameName.Trim();

            if (name.Length == 0)
                return "Game name cannot be empty.";

            var duplicate = Profiles.Any(item =>
                item.Profile.Id != _editingProfileId &&
                string.Equals(item.GameName, name, StringComparison.OrdinalIgnoreCase));

            if (duplicate)
                return $"A profile named \"{name}\" already exists.";

            if (string.IsNullOrWhiteSpace(EditVortexStagingPath) &&
                string.IsNullOrWhiteSpace(EditGameRootPath) &&
                customFolders.Count == 0)
            {
                return "Specify at least one source: Vortex staging folder, game root or a custom folder.";
            }

            return null;
        }

        private static string? NullIfEmpty(string value)
        {
            var trimmed = value.Trim();

            return trimmed.Length == 0 ? null : trimmed;
        }

        private static List<string> ParseLines(string text)
        {
            return text
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}