using System.ComponentModel;
using System.Windows;

using UniversalBackupTool.UI.Helpers;
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
            Closing += OnClosing;
            _viewModel.CloseRequested += (_, _) => Dispatcher.BeginInvoke(Close);
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            RootNavigation.Navigate(typeof(DashboardPage));

            await _viewModel.InitializeCommand.ExecuteAsync(null);
        }

        private void OnClosing(object? sender, CancelEventArgs e)
        {
            // A running backup/restore/purge is cancelled first so it can clean up its temporary files.
            if (!_viewModel.TryPrepareClose())
                e.Cancel = true;
        }

        private void OnCopyAllOutputClick(object sender, RoutedEventArgs e)
        {
            OutputListBehavior.CopyToClipboard(_viewModel.OutputEntries);
        }
    }
}
