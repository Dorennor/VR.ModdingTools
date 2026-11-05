using CommunityToolkit.Mvvm.ComponentModel;

using UniversalBackupTool.Model.Models.Profiles;

namespace UniversalBackupTool.ViewModel.ViewModels.Backup
{
    public sealed partial class BackupTargetItemViewModel : ObservableObject
    {
        public BackupTargetItemViewModel(BackupProfile profile, bool isSelected = false)
        {
            Profile = profile ?? throw new ArgumentNullException(nameof(profile));
            _isSelected = isSelected;
            _presence = GamePresence.Unknown(profile.Id);
        }

        public BackupProfile Profile { get; }

        public string GameName => Profile.GameName;

        [ObservableProperty]
        private bool _isSelected;

        /// <summary>
        /// False while games of the other section (Vortex / not Vortex) are selected; a backup never mixes the two.
        /// </summary>
        [ObservableProperty]
        private bool _isSelectable = true;

        /// <summary>
        /// What is on this PC of the game; decides its label and which components it can use.
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DisplayName))]
        [NotifyPropertyChangedFor(nameof(IsDimmed))]
        private GamePresence _presence;

        /// <summary>
        /// False while "Hide uninstalled games" hides this game.
        /// </summary>
        public bool IsListed { get; set; } = true;

        public string DisplayName => Presence.IsInstalled
            ? GameName
            : Presence.HasStaging ? $"{GameName} (mods only)" : $"{GameName} (not installed)";

        /// <summary>
        /// Dims the name of a game that is not installed and has no mods here. A "mods only" game can still be backed up.
        /// </summary>
        public bool IsDimmed => !Presence.IsListed;

        public bool CanBackUpStaging => Presence.HasStaging;

        public bool CanBackUpGameFolder => Presence.HasGameFolder;

        public bool CanBackUpFullGame => Presence.HasGameFolder && Profile.GetFullGameExportFolder() is not null;

        public bool CanBackUpCustomPaths => Presence.HasCustomPaths;
    }
}