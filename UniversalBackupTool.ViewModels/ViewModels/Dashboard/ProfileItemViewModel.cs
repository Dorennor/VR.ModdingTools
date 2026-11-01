using CommunityToolkit.Mvvm.ComponentModel;

using UniversalBackupTool.Model.Models.Profiles;

namespace UniversalBackupTool.ViewModel.ViewModels.Dashboard
{
    public sealed class ProfileItemViewModel : ObservableObject
    {
        public ProfileItemViewModel(BackupProfile profile)
        {
            Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        }

        public BackupProfile Profile { get; }

        public string GameName => Profile.GameName;

        public string? VortexStagingPath => Profile.VortexStagingPath;

        public string? GameRootPath => Profile.GameRootPath;

        public bool UseIgnoreList => Profile.UseIgnoreList;

        public bool UsesVortex => Profile.UsesVortex;

        public bool HasVortexStagingPath => Profile.UsesVortex && !string.IsNullOrWhiteSpace(Profile.VortexStagingPath);

        public bool HasGameRootPath => !string.IsNullOrWhiteSpace(Profile.GameRootPath);

        public bool HasCustomFolders => Profile.CustomFolders.Count > 0;

        public string CustomFoldersText => string.Join(Environment.NewLine, Profile.CustomFolders);
    }
}