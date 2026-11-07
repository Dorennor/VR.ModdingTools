using System.Windows;
using System.Windows.Controls;

using UniversalBackupTool.ViewModel.ViewModels.Dashboard;

namespace UniversalBackupTool.UI.Views
{
    public partial class DashboardPage : Page
    {
        private readonly DashboardViewModel _viewModel;

        public DashboardPage(DashboardViewModel viewModel)
        {
            _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

            DataContext = _viewModel;

            InitializeComponent();

            Loaded += OnLoaded;

            // The editor sits at the top of the page while the Edit buttons are on the profile cards further down.
            _viewModel.EditorOpened += (_, _) => PageScrollViewer.ScrollToTop();
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            await _viewModel.LoadCommand.ExecuteAsync(null);
        }
    }
}