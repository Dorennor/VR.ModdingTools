using CommunityToolkit.Mvvm.ComponentModel;

using UniversalBackupTool.Model.Models.Profiles;

namespace UniversalBackupTool.ViewModel.ViewModels.BackupRestore
{
    public sealed partial class BackupTargetItemViewModel : ObservableObject
    {
        public BackupTargetItemViewModel(BackupProfile profile, bool isSelected = false)
        {
            Profile = profile ?? throw new ArgumentNullException(nameof(profile));
            _isSelected = isSelected;
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
        /// Whether the game is on this PC. Only "Show only installed games" (Settings) ever makes this anything but OnDisk.
        /// </summary>
        public GamePresenceState Presence { get; set; } = GamePresenceState.OnDisk;

        public bool IsOnDisk => Presence == GamePresenceState.OnDisk;
    }
}