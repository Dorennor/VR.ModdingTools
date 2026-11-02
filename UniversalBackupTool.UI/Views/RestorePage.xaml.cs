using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

using UniversalBackupTool.ViewModel.ViewModels.Restore;

namespace UniversalBackupTool.UI.Views
{
    public partial class RestorePage : Page
    {
        private readonly RestoreViewModel _viewModel;

        public RestorePage(RestoreViewModel viewModel)
        {
            _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

            DataContext = _viewModel;

            InitializeComponent();

            Loaded += OnLoaded;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            await _viewModel.LoadCommand.ExecuteAsync(null);
        }

        /// <summary>
        /// The options open below the list, which can be longer than the window: without this a click on
        /// "Restore from this" could seem to do nothing.
        /// </summary>
        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(RestoreViewModel.RestoreFolderPath) && _viewModel.RestoreFolderPath is not null)
                Dispatcher.InvokeAsync(() => RestoreOptionsPanel.BringIntoView(), DispatcherPriority.Loaded);
        }
    }
}