using System.Windows;
using System.Windows.Controls;

using UniversalBackupTool.ViewModel.ViewModels.Backup;

namespace UniversalBackupTool.UI.Views
{
    public partial class BackupPage : Page
    {
        private readonly BackupViewModel _viewModel;

        public BackupPage(BackupViewModel viewModel)
        {
            _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

            DataContext = _viewModel;

            InitializeComponent();

            Loaded += OnLoaded;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            await _viewModel.LoadCommand.ExecuteAsync(null);
        }
    }
}