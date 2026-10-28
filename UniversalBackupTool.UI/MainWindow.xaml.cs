using System.Windows;

using UniversalBackupTool.UI.Views;
using UniversalBackupTool.ViewModel.ViewModels.Main;

using Wpf.Ui.Abstractions;
using Wpf.Ui.Controls;

namespace UniversalBackupTool.UI
{
    public partial class MainWindow : FluentWindow
    {
        private readonly MainViewModel _viewModel;

        public MainWindow(MainViewModel viewModel, INavigationViewPageProvider pageProvider)
        {
            _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            ArgumentNullException.ThrowIfNull(pageProvider);

            DataContext = _viewModel;

            InitializeComponent();

            RootNavigation.SetPageProviderService(pageProvider);

            Loaded += OnLoaded;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            RootNavigation.Navigate(typeof(DashboardPage));

            await _viewModel.InitializeCommand.ExecuteAsync(null);
        }
    }
}