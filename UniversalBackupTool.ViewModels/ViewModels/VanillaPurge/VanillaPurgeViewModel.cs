using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using UniversalBackupTool.Interface.Interfaces.Dialogs;
using UniversalBackupTool.Interface.Interfaces.Operations;
using UniversalBackupTool.Interface.Interfaces.Profiles;
using UniversalBackupTool.Interface.Interfaces.Settings;
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
        private readonly ISettingsStore _settingsStore;
        private readonly ITrackedFilesPurgeService _trackedFilesPurgeService;

        private StoredSnapshot? _currentSnapshot;

        public VanillaPurgeViewModel(
            IProfileStore profileStore,
            ISnapshotService snapshotService,
            ISnapshotStore snapshotStore,
            IAppStoragePathsProvider storagePathsProvider,
            IConfirmationService confirmationService,
            ISettingsStore settingsStore,
            ITrackedFilesPurgeService trackedFilesPurgeService,
            IOperationCoordinator operationCoordinator,
            IOperationOutputService operationOutputService)
            : base(operationOutputService, operationCoordinator)
        {
            _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
            _trackedFilesPurgeService = trackedFilesPurgeService ?? throw new ArgumentNullException(nameof(trackedFilesPurgeService));
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

                    foreach (var profile in profiles
                                 .Where(profile => !string.IsNullOrWhiteSpace(profile.GameRootPath))
                                 .OrderBy(profile => profile.GameName, StringComparer.CurrentCultureIgnoreCase))
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

                var snapshot = await Task.Run(() => _snapshotService.CreateSnapshotAsync(profile.Id, rootPath, scope.CancellationToken, scope), scope.CancellationToken);

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

                var unhashed = snapshot.Files.Values.Count(file => !file.IsLink && file.Sha256 is null);

                if (unhashed > 0)
                    ReportWarning($"{profile.GameName}: {unhashed:N0} file(s) could not be read (in use or access denied) and have no content hash. If they get new timestamps later, Changed game files backs them up without a content check. Close the game and launchers and recreate the snapshot to hash them.");

                var links = snapshot.Files.Values.Count(file => file.IsLink);

                if (links > 0)
                    ReportWarning($"{profile.GameName}: the snapshot contains {links:N0} linked file(s) (hardlinks/symlinks, usually Vortex deployments). A vanilla snapshot should be taken with mods undeployed (Purge in Vortex first); otherwise Purge treats these links as vanilla files and keeps them.");
            });
        }

        /// <summary>
        /// Adds what the stored snapshot lacks from the files on disk, for files proven to be the vanilla file. What was
        /// already verified is saved also when the operation is cancelled.
        /// </summary>
        [RelayCommand]
        private async Task UpgradeSnapshotAsync()
        {
            var target = GetPurgeTarget();

            if (target is null)
                return;

            await RunOperationAsync("Upgrade Snapshot", async scope =>
            {
                var snapshotsDirectory = _storagePathsProvider.GetPaths().SnapshotsDirectory;
                var upgraded = new List<FileMetadata>();

                scope.Report(new($"scanning {target.RootPath}", null, 0, 0, 0, 0));

                SnapshotUpgradeResult result;

                try
                {
                    result = await Task.Run(
                        () => _snapshotService.UpgradeSnapshotAsync(target.RootPath, target.Snapshot, scope.CancellationToken, scope, upgraded.Add),
                        scope.CancellationToken);
                }
                finally
                {
                    await _snapshotStore.UpdateEntriesAsync(snapshotsDirectory, target.Profile.Id, upgraded, CancellationToken.None);

                    if (SelectedProfile?.Profile.Id == target.Profile.Id)
                        await LoadSnapshotAsync(target.Profile.Id);
                }

                ReportSuccess($"{target.Profile.GameName}: snapshot upgraded, {result.Upgraded:N0} file(s) got new details, {result.AlreadyComplete:N0} needed nothing.");

                if (result.Different.Count > 0)
                {
                    ReportWarning($"{target.Profile.GameName}: {result.Different.Count:N0} file(s) differ from the snapshot and were left as they are in it (changed by a mod or a game update):");

                    foreach (var path in result.Different.Take(20))
                        ReportWarning("    " + path);
                }

                if (result.Missing > 0)
                    ReportInfo($"{target.Profile.GameName}: {result.Missing:N0} file(s) of the snapshot are not in the game folder, so they got no new details.");

                if (result.Links > 0)
                    ReportWarning($"{target.Profile.GameName}: {result.Links:N0} file(s) are links (usually Vortex deployments) and got no new details. Purge the mods in Vortex and run this again.");

                if (result.Unreadable > 0)
                    ReportWarning($"{target.Profile.GameName}: {result.Unreadable:N0} file(s) could not be read (in use or access denied). Close the game and launchers and run this again.");

                if (result.CannotProve > 0)
                    ReportWarning($"{target.Profile.GameName}: {result.CannotProve:N0} file(s) have no hash in the snapshot and other timestamps on disk, so nothing proves they are the vanilla files. They got no new details; only a new snapshot of a vanilla game folder can fix them.");

                if (target.Snapshot.Directories is null)
                    ReportWarning($"{target.Profile.GameName}: this snapshot has no folder list. It cannot be added from the disk, because the folders there may come from mods; only a new snapshot of a vanilla game folder records it.");
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
                var plan = await FindUnregisteredAsync(target, scope);
                ShowPreviewIfStillSelected(target, plan);
                ReportInfo($"{target.Profile.GameName}: {DescribeUnregistered(plan)} would be deleted from {target.RootPath}.");
                ReportLegacySnapshotFolders(target, plan);
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
                var plan = await FindUnregisteredAsync(target, scope);
                ShowPreviewIfStillSelected(target, plan);

                if (plan.IsEmpty)
                {
                    ReportSuccess($"{target.Profile.GameName}: nothing to purge, the game folder matches the baseline snapshot.");
                    return;
                }

                var confirmed = _confirmationService.Confirm(
                    "Execute Safe Purge",
                    $"{DescribeUnregistered(plan)} not present in the baseline snapshot will be deleted from:\n{target.RootPath}\n\n"
                    + (plan.MovedCount > 0 ? $"{plan.MovedCount:N0} vanilla file(s) found at another path will be moved back to their original path instead of being deleted.\n" : string.Empty)
                    + "For links (Vortex hardlinks/symlinks, junctions) only the link is removed; the files they point to are not touched.\n"
                    + "A Safety Backup of the other files is created first. Continue?");

                if (!confirmed)
                {
                    ReportInfo("Purge was not confirmed; nothing was deleted.");
                    return;
                }

                ReportLegacySnapshotFolders(target, plan);
                scope.Report(new("creating safety backup and deleting files", null, 0, 0, 0, plan.Files.Count));

                var result = await Task.Run(() => _snapshotService.PurgeUnregisteredFilesAsync(target.RootPath, target.Snapshot, scope.CancellationToken), scope.CancellationToken);

                if (SelectedProfile?.Profile.Id == target.Profile.Id)
                    ClearPreview();

                if (result.SafetyBackupPath is not null)
                    ReportInfo($"Deleted files were saved to {result.SafetyBackupPath}");

                var folders = result.FoldersRemoved > 0 ? $", {result.FoldersRemoved:N0} empty folder(s) removed" : string.Empty;

                ReportSuccess(result.LinksRemoved > 0
                    ? $"Purge completed for {target.Profile.GameName}: {result.FilesDeleted:N0} entries deleted, {result.LinksRemoved:N0} of them links (only the links were removed, their targets are untouched){folders}."
                    : $"Purge completed for {target.Profile.GameName}: {result.FilesDeleted:N0} file(s) deleted{folders}.");

                if (result.FoldersKept > 0)
                    ReportWarning($"{target.Profile.GameName}: {result.FoldersKept:N0} folder(s) not in the snapshot were kept because they are not empty or could not be removed (in use or access denied).");

                if (result.FilesMovedBack > 0)
                    ReportInfo($"{target.Profile.GameName}: {result.FilesMovedBack:N0} vanilla file(s) found at another path were moved back to their original path.");

                if (result.FilesNotMovedBack.Count > 0)
                {
                    ReportWarning($"{target.Profile.GameName}: {result.FilesNotMovedBack.Count:N0} vanilla file(s) at another path could not be moved back (in use or access denied) and were left there:");

                    foreach (var path in result.FilesNotMovedBack.Take(20))
                        ReportWarning("    " + path);
                }
            });
        }

        [RelayCommand]
        private async Task PreviewTrackedAsync()
        {
            var target = GetPurgeTarget();

            if (target is null)
                return;

            await RunOperationAsync("Tracked Purge Preview", async scope =>
            {
                var plan = await BuildTrackedPlanAsync(target, scope);

                if (plan is null)
                    return;

                ShowTrackedPreviewIfStillSelected(target, plan);
                ReportTrackedPlan(target, plan);
            });
        }

        [RelayCommand]
        private async Task PurgeTrackedAsync()
        {
            var target = GetPurgeTarget();

            if (target is null)
                return;

            await RunOperationAsync("Tracked Purge", async scope =>
            {
                var plan = await BuildTrackedPlanAsync(target, scope);

                if (plan is null)
                    return;

                ShowTrackedPreviewIfStillSelected(target, plan);
                ReportTrackedPlan(target, plan);

                var removeCount = plan.Count(TrackedFileStatus.Remove);

                if (removeCount == 0 && plan.Folders.Count == 0)
                {
                    ReportSuccess($"{target.Profile.GameName}: nothing to purge, no file from the backups is left unchanged in the game folder.");
                    return;
                }

                var confirmed = _confirmationService.Confirm(
                    "Purge tracked files",
                    $"{removeCount:N0} file(s) from the backups of {target.Profile.GameName} will be deleted from:\n{target.RootPath}\n"
                    + (plan.Folders.Count > 0 ? $"and up to {plan.Folders.Count:N0} folder(s) recorded in the backups, if they are empty afterwards.\n" : string.Empty)
                    + "\nOnly files whose content still matches the backup are deleted. Original game files, Vortex links (purge them in Vortex) and files changed since the backup are kept.\n"
                    + "A Safety Backup of the deleted files is created first. Continue?");

                if (!confirmed)
                {
                    ReportInfo("Tracked purge was not confirmed; nothing was deleted.");
                    return;
                }

                scope.Report(new("creating safety backup and deleting files", null, 0, 0, 0, removeCount));

                var result = await Task.Run(() => _trackedFilesPurgeService.PurgeAsync(plan, scope.CancellationToken), scope.CancellationToken);

                if (SelectedProfile?.Profile.Id == target.Profile.Id)
                    ClearPreview();

                if (result.SafetyBackupPath is not null)
                    ReportInfo($"Deleted files were saved to {result.SafetyBackupPath}");

                var folders = result.FoldersRemoved > 0 ? $", {result.FoldersRemoved:N0} empty folder(s) removed" : string.Empty;
                ReportSuccess($"Tracked purge completed for {target.Profile.GameName}: {result.FilesDeleted:N0} file(s) deleted{folders}.");

                if (result.FilesKept.Count > 0)
                {
                    ReportWarning($"{target.Profile.GameName}: {result.FilesKept.Count:N0} file(s) were kept because they changed since the preview, are in use or access was denied:");

                    foreach (var path in result.FilesKept.Take(20))
                        ReportWarning("    " + path);
                }

                if (result.FoldersKept > 0)
                    ReportInfo($"{target.Profile.GameName}: {result.FoldersKept:N0} folder(s) from the backups were kept because they still hold files.");

                ReportInfo("Next: let the game update (verify the game files if original files were reported), create a new vanilla snapshot, deploy the mods in Vortex and restore the configs.");
            });
        }

        /// <summary>
        /// The plan from the newest complete Changed game files, New game files and Game configs backups, or null (with an error) when the
        /// backup destination is not set or the game has no such backup.
        /// </summary>
        private async Task<TrackedPurgePlan?> BuildTrackedPlanAsync(PurgeTarget target, IOperationScope scope)
        {
            var settings = await _settingsStore.LoadAsync(_storagePathsProvider.GetPaths().SettingsFilePath, scope.CancellationToken);

            if (string.IsNullOrWhiteSpace(settings.GoogleDriveBackupPath))
            {
                ReportError("The backup destination folder is not configured. Set it on the Settings page.");
                return null;
            }

            scope.Report(new($"reading the backups of {target.Profile.GameName}", null, 0, 0, 0, 0));

            var plan = await Task.Run(
                () => _trackedFilesPurgeService.BuildPlanAsync(target.Profile, target.RootPath, target.Snapshot, settings.GoogleDriveBackupPath, scope, scope.CancellationToken),
                scope.CancellationToken);

            if (!plan.HasBackups)
            {
                ReportError($"{target.Profile.GameName}: no complete Changed game files, New game files or Game configs backup found in {settings.GoogleDriveBackupPath}. Back up one of them first.");
                return null;
            }

            return plan;
        }

        private void ReportTrackedPlan(PurgeTarget target, TrackedPurgePlan plan)
        {
            foreach (var backup in plan.Backups)
                ReportInfo($"{target.Profile.GameName}: using the backup of {backup.Manifest.CreatedAt:g} in {backup.Directory}");

            ReportInfo($"{target.Profile.GameName}: {DescribeTrackedPlan(plan)}");

            if (plan.Count(TrackedFileStatus.VortexLink) > 0)
                ReportWarning($"{target.Profile.GameName}: {plan.Count(TrackedFileStatus.VortexLink):N0} Vortex link(s) are still deployed. Purge the mods in Vortex first; this purge leaves links to Vortex.");

            if (plan.Count(TrackedFileStatus.OriginalGameFile) > 0)
                ReportWarning($"{target.Profile.GameName}: {plan.Count(TrackedFileStatus.OriginalGameFile):N0} original game file(s) were overwritten by mods and are kept. Verify the game files (Steam: Verify integrity) to get the original versions back.");

            if (target.Snapshot.Directories is null && plan.Folders.Count > 0)
                ReportWarning($"{target.Profile.GameName}: this snapshot was created before folders were recorded, so an empty vanilla folder may count as a mod folder. Check the folder list.");
        }

        private static string DescribeTrackedPlan(TrackedPurgePlan plan)
        {
            var kept = new List<string>();

            void AddKept(TrackedFileStatus status, string label)
            {
                var count = plan.Count(status);

                if (count > 0)
                    kept.Add($"{count:N0} {label}");
            }

            AddKept(TrackedFileStatus.ChangedSinceBackup, "changed since the backup");
            AddKept(TrackedFileStatus.OriginalGameFile, "original game file(s)");
            AddKept(TrackedFileStatus.VortexLink, "Vortex link(s)");
            AddKept(TrackedFileStatus.CannotVerify, "that cannot be verified");

            var folders = plan.Folders.Count > 0 ? $" and up to {plan.Folders.Count:N0} folder(s)" : string.Empty;
            var keptText = kept.Count > 0 ? $"; kept: {string.Join(", ", kept)}" : string.Empty;
            var gone = plan.FilesAlreadyGone > 0 ? $"; {plan.FilesAlreadyGone:N0} file(s) from the backups are already gone" : string.Empty;

            return $"{plan.Count(TrackedFileStatus.Remove):N0} file(s){folders} would be removed{keptText}{gone}.";
        }

        private void ShowTrackedPreviewIfStillSelected(PurgeTarget target, TrackedPurgePlan plan)
        {
            if (SelectedProfile?.Profile.Id != target.Profile.Id)
                return;

            static string Describe(TrackedFileStatus status) => status switch
            {
                TrackedFileStatus.Remove => "removed",
                TrackedFileStatus.ChangedSinceBackup => "kept: changed since the backup",
                TrackedFileStatus.OriginalGameFile => "kept: original game file, verify the game files",
                TrackedFileStatus.VortexLink => "kept: Vortex link, purge it in Vortex",
                _ => "kept: cannot verify"
            };

            PreviewFiles = plan.Files
                .OrderBy(file => file.Status)
                .ThenBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
                .Select(file => $"{file.RelativePath}    ({Describe(file.Status)})")
                .Concat(plan.Folders.OrderBy(folder => folder, StringComparer.OrdinalIgnoreCase).Select(folder => $"{folder}/    (folder: removed when empty)"))
                .ToList();

            PreviewSummary = plan.Files.Count == 0 && plan.Folders.Count == 0
                ? "No file from the backups is left in the game folder."
                : DescribeTrackedPlan(plan);
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

        private async Task<PurgePlan> FindUnregisteredAsync(PurgeTarget target, IOperationScope scope)
        {
            scope.Report(new($"comparing {target.RootPath} with the baseline snapshot", null, 0, 0, 0, 0));

            var changes = await Task.Run(() => _snapshotService.GetChangesAsync(target.RootPath, target.Snapshot, scope.CancellationToken), scope.CancellationToken);
            var folders = await Task.Run(() => _snapshotService.GetUnregisteredDirectoriesAsync(target.RootPath, target.Snapshot, scope.CancellationToken), scope.CancellationToken);
            var relocated = await Task.Run(() => _snapshotService.FindRelocatedFilesAsync(target.RootPath, target.Snapshot, scope.CancellationToken, scope), scope.CancellationToken);

            // Changes also contain modified and deleted files, which a purge never touches.
            var files = changes
                .Where(change => !target.Snapshot.Files.ContainsKey(change.Key))
                .Select(change => change.Value)
                .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new PurgePlan(
                files,
                folders.OrderBy(folder => folder, StringComparer.OrdinalIgnoreCase).ToList(),
                relocated.ToDictionary(file => file.RelativePath, StringComparer.OrdinalIgnoreCase));
        }

        private static string DescribeUnregistered(PurgePlan plan)
        {
            var count = plan.Files.Count - plan.MovedCount;
            var links = plan.Files.Count(file => file.IsLink);
            var files = links == 0
                ? $"{count:N0} unregistered file(s)"
                : $"{count:N0} unregistered entries ({links:N0} of them links)";

            return plan.Folders.Count == 0 ? files : $"{files} and {plan.Folders.Count:N0} folder(s)";
        }

        /// <summary>
        /// Snapshots from before folders were recorded only know the folders holding vanilla files, so a vanilla folder
        /// that was empty counts as unregistered.
        /// </summary>
        private void ReportLegacySnapshotFolders(PurgeTarget target, PurgePlan plan)
        {
            if (target.Snapshot.Directories is null && plan.Folders.Count > 0)
                ReportWarning($"{target.Profile.GameName}: this snapshot was created before folders were recorded, so any folder without vanilla files counts as unregistered (including a vanilla folder that was empty). Check the folder list; after purging, recreate the snapshot so folders are recorded.");
        }

        private void ShowPreviewIfStillSelected(PurgeTarget target, PurgePlan plan)
        {
            if (SelectedProfile?.Profile.Id == target.Profile.Id)
                ShowPreview(plan);
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

        private void ShowPreview(PurgePlan plan)
        {
            string Describe(FileMetadata file)
            {
                if (file.IsLink)
                    return $"{file.RelativePath}    (link: only the link is removed)";

                if (!plan.Relocated.TryGetValue(file.RelativePath, out var relocated))
                    return file.RelativePath;

                return relocated.OriginalMissing
                    ? $"{file.RelativePath}    (vanilla file moved from {relocated.OriginalPath}: moved back)"
                    : $"{file.RelativePath}    (copy of vanilla file {relocated.OriginalPath}: deleted)";
            }

            PreviewFiles = plan.Files
                .Select(Describe)
                .Concat(plan.Folders.Select(folder => $"{folder}/    (folder: removed when empty)"))
                .ToList();

            PreviewSummary = plan.IsEmpty
                ? "No unregistered files found."
                : $"{DescribeUnregistered(plan)} would be deleted{(plan.MovedCount > 0 ? $", {plan.MovedCount:N0} moved vanilla file(s) moved back" : string.Empty)}.";
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
            var lacking = _currentSnapshot?.Data.Files.Values.Count(file => !file.IsLink && (file.Sha256 is null || file.CreationTimeUtc is null)) ?? 0;
            var lackingText = lacking > 0 ? $" {lacking:N0} of them lack details; use Add missing details." : string.Empty;

            SnapshotStatus = SelectedProfile is null
                ? "Select a profile."
                : _currentSnapshot is null
                    ? "No baseline snapshot yet. Create one while the game folder is in a clean, vanilla state."
                    : $"Created {_currentSnapshot.CreatedAt:g}. Contains {_currentSnapshot.Data.Files.Count:N0} files.{lackingText}";

            OnPropertyChanged(nameof(HasSnapshot));
        }

        private sealed record PurgeTarget(BackupProfile Profile, string RootPath, SnapshotData Snapshot);

        /// <summary>
        /// Entries not in the baseline snapshot, and the folders not in it (removed by Purge once they are empty).
        /// </summary>
        /// <param name="Relocated">Files among <paramref name="Files"/> with the content of a vanilla file, keyed by path.</param>
        private sealed record PurgePlan(List<FileMetadata> Files, List<string> Folders, Dictionary<string, RelocatedFile> Relocated)
        {
            public bool IsEmpty => Files.Count == 0 && Folders.Count == 0;

            /// <summary>
            /// Vanilla files at another path, which Purge moves back instead of deleting.
            /// </summary>
            public int MovedCount => Relocated.Values.Count(file => file.OriginalMissing);
        }
    }
}