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
    }
}