using CommunityToolkit.Mvvm.ComponentModel;

using UniversalBackupTool.Interface.Interfaces.Paths;

namespace UniversalBackupTool.ViewModel.ViewModels.Dashboard
{
    /// <summary>
    /// One custom path in the profile editor: a folder or a single file to back up.
    /// </summary>
    public sealed partial class CustomPathItemViewModel : ObservableObject
    {
        private readonly IPathDriveService _pathDriveService;

        public CustomPathItemViewModel(string path, IPathDriveService pathDriveService)
        {
            _pathDriveService = pathDriveService ?? throw new ArgumentNullException(nameof(pathDriveService));
            _path = path ?? string.Empty;
        }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Drive))]
        [NotifyPropertyChangedFor(nameof(IsFile))]
        [NotifyPropertyChangedFor(nameof(IsMissing))]
        [NotifyPropertyChangedFor(nameof(KindLabel))]
        private string _path;

        public bool IsFile => File.Exists(Path);

        public bool IsMissing => !string.IsNullOrWhiteSpace(Path) && !File.Exists(Path) && !Directory.Exists(Path);

        public string KindLabel => IsFile ? "File" : IsMissing ? "Not found" : "Folder";

        /// <summary>
        /// Drive letter of the path; setting it swaps only the drive, like the other profile paths.
        /// </summary>
        public string? Drive
        {
            get => _pathDriveService.GetDrive(Path);
            set
            {
                if (string.IsNullOrWhiteSpace(value) || _pathDriveService.GetDrive(Path) is null)
                    return;

                try
                {
                    Path = _pathDriveService.ReplaceDrive(Path, value);
                }
                catch (ArgumentException)
                {
                    OnPropertyChanged(nameof(Drive));
                }
            }
        }
    }
}
