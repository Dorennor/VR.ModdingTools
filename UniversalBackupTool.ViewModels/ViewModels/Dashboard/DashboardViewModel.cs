using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using UniversalBackupTool.Interface.Interfaces.Dialogs;
using UniversalBackupTool.Interface.Interfaces.Operations;
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
        private readonly IFilePickerService _filePickerService;

        private Guid? _editingProfileId;
        private BackupProfile? _editingOriginal;

        public DashboardViewModel(
            IProfileStore profileStore,
            IPathDriveService pathDriveService,
            ISystemPathsProvider systemPathsProvider,
            IAppStoragePathsProvider storagePathsProvider,
            IFolderPickerService folderPickerService,
            IFilePickerService filePickerService,
            IOperationCoordinator operationCoordinator,
            IOperationOutputService operationOutputService)
            : base(operationOutputService, operationCoordinator)
        {
            _profileStore = profileStore ?? throw new ArgumentNullException(nameof(profileStore));
            _pathDriveService = pathDriveService ?? throw new ArgumentNullException(nameof(pathDriveService));
            _systemPathsProvider = systemPathsProvider ?? throw new ArgumentNullException(nameof(systemPathsProvider));
            _storagePathsProvider = storagePathsProvider ?? throw new ArgumentNullException(nameof(storagePathsProvider));
            _folderPickerService = folderPickerService ?? throw new ArgumentNullException(nameof(folderPickerService));
            _filePickerService = filePickerService ?? throw new ArgumentNullException(nameof(filePickerService));

            AvailableDrives = _systemPathsProvider.GetAvailableDrives();
            EditCustomPaths.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoEditCustomPaths));
        }

        /// <summary>
        /// Custom paths of the profile being edited: folders or single files, each backed up as its own unit.
        /// </summary>
        public ObservableCollection<CustomPathItemViewModel> EditCustomPaths { get; } = [];

        public bool HasNoEditCustomPaths => EditCustomPaths.Count == 0;

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

        public ObservableCollection<ProfilePathEntry> FindablePaths { get; } = [];

        public ObservableCollection<string> FoundCandidates { get; } = [];

        public bool HasFindMessage => !string.IsNullOrEmpty(FindMessage);

        public bool HasCandidates => FoundCandidates.Count > 0;

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

        partial void OnEditVortexStagingPathChanged(string value) => OnPropertyChanged(nameof(EditVortexStagingDrive));

        partial void OnEditGameRootPathChanged(string value) => OnPropertyChanged(nameof(EditGameRootDrive));

        partial void OnEditVortexAppDataPathChanged(string value) => OnPropertyChanged(nameof(EditVortexAppDataDrive));

        partial void OnSelectedFindPathChanged(ProfilePathEntry? value) => ClearFindResults();

        [RelayCommand]
        private Task LoadAsync() => RunBusyAsync(async () =>
                {
                    var profiles = await _profileStore.LoadAsync(ProfilesFilePath);

                    ReplaceProfiles(profiles);
                });

        [RelayCommand]
        private void AddProfile()
        {
            _editingProfileId = null;
            _editingOriginal = null;

            EditGameName = string.Empty;
            EditVortexStagingPath = string.Empty;
            EditGameRootPath = string.Empty;
            EditVortexAppDataPath = _systemPathsProvider.GetDefaultVortexAppDataPath();
            SetEditCustomPaths([]);
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

            var profile = item.Profile;

            _editingProfileId = profile.Id;
            _editingOriginal = profile;

            EditGameName = profile.GameName;
            EditVortexStagingPath = profile.VortexStagingPath ?? string.Empty;
            EditGameRootPath = profile.GameRootPath ?? string.Empty;
            EditVortexAppDataPath = profile.VortexAppDataPath ?? string.Empty;
            SetEditCustomPaths(profile.CustomFolders);
            EditUseIgnoreList = profile.UseIgnoreList;

            IsFindVisible = false;
            IsEditing = true;
            OnPropertyChanged(nameof(EditorTitle));
        }

        [RelayCommand]
        private void CancelEdit()
        {
            IsEditing = false;
        }

        [RelayCommand]
        private Task SaveProfileAsync() => RunBusyAsync(async () =>
                {
                    var customFolders = EditCustomPaths
                        .Select(item => item.Path.Trim())
                        .Where(path => path.Length > 0)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    var validationError = ValidateEditor(customFolders);

                    if (validationError is not null)
                    {
                        ReportError(validationError);
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
                    ReportSuccess($"Profile \"{edited.GameName}\" saved.");
                });

        [RelayCommand]
        private Task DeleteProfileAsync(ProfileItemViewModel? item) => RunBusyAsync(async () =>
                {
                    if (item is null)
                        return;

                    var updated = Profiles
                        .Select(profileItem => profileItem.Profile)
                        .Where(profile => profile.Id != item.Profile.Id)
                        .ToList();

                    await _profileStore.SaveAsync(ProfilesFilePath, updated);

                    ReplaceProfiles(updated);

                    if (_editingProfileId == item.Profile.Id)
                        IsEditing = false;

                    ReportSuccess($"Profile \"{item.GameName}\" deleted.");
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
            PickInto(LastCustomDirectory(), AddCustomPath);
        }

        [RelayCommand]
        private void AddCustomFiles()
        {
            foreach (var path in _filePickerService.PickFiles(LastCustomDirectory()))
                AddCustomPath(path);
        }

        [RelayCommand]
        private void RemoveCustomPath(CustomPathItemViewModel? item)
        {
            if (item is not null)
                EditCustomPaths.Remove(item);
        }

        private void AddCustomPath(string path)
        {
            if (EditCustomPaths.Any(item => string.Equals(item.Path.Trim(), path.Trim(), StringComparison.OrdinalIgnoreCase)))
                return;

            EditCustomPaths.Add(new CustomPathItemViewModel(path, _pathDriveService));
        }

        private void SetEditCustomPaths(IEnumerable<string> paths)
        {
            EditCustomPaths.Clear();

            foreach (var path in paths)
                AddCustomPath(path);
        }

        /// <summary>
        /// Start the pickers where the last custom path lives, so adding several entries from one place is quick.
        /// </summary>
        private string? LastCustomDirectory()
        {
            var last = EditCustomPaths.LastOrDefault()?.Path;

            if (string.IsNullOrWhiteSpace(last))
                return null;

            return File.Exists(last) ? Path.GetDirectoryName(last) : last;
        }

        [RelayCommand]
        private void ToggleFind()
        {
            IsFindVisible = !IsFindVisible;

            if (!IsFindVisible)
                return;

            IsEditing = false;
            RebuildFindablePaths();
        }

        [RelayCommand]
        private Task FindOnOtherDisksAsync() => RunBusyAsync(async () =>
                {
                    ClearFindResults();

                    if (SelectedFindPath is null)
                    {
                        ReportError("Select a path to look for.");
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
                    if (SelectedFindPath is null || string.IsNullOrWhiteSpace(SelectedCandidate))
                    {
                        ReportError("Select the found path to save.");
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
                    ReportSuccess($"Path for \"{entry.GameName}\" ({entry.KindLabel}) changed to {candidate}.");
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
                ReportError(exception.Message);
                OnPropertyChanged(propertyName);
            }
        }

        private void ReplaceProfiles(IEnumerable<BackupProfile> profiles)
        {
            Profiles.Clear();

            foreach (var profile in profiles.OrderBy(profile => profile.GameName, StringComparer.CurrentCultureIgnoreCase))
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
                return "Specify at least one source: Vortex staging folder, game folder or a custom path.";
            }

            return null;
        }

        private static string? NullIfEmpty(string value)
        {
            var trimmed = value.Trim();

            return trimmed.Length == 0 ? null : trimmed;
        }
    }
}