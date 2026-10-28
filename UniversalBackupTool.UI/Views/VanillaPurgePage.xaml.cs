using System.Windows;
using System.Windows.Controls;

using UniversalBackupTool.ViewModel.ViewModels.VanillaPurge;

namespace UniversalBackupTool.UI.Views
{
    public partial class VanillaPurgePage : Page
    {
        private readonly VanillaPurgeViewModel _viewModel;

        public VanillaPurgePage(VanillaPurgeViewModel viewModel)
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