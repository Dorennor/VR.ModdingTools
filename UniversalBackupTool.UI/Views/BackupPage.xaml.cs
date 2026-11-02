using System.Windows;
using System.Windows.Controls;

using UniversalBackupTool.ViewModel.ViewModels.BackupRestore;

namespace UniversalBackupTool.UI.Views
{
    public partial class BackupRestorePage : Page
    {
        private readonly BackupRestoreViewModel _viewModel;

        public BackupRestorePage(BackupRestoreViewModel viewModel)
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