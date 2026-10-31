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
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;

            // Pages must end right above the docked Output/status bar instead of being covered by it.
            BottomDock.SizeChanged += (_, _) => UpdatePageAreaMargin();
            RootNavigation.Loaded += (_, _) => UpdatePageAreaMargin();
        }

        /// <summary>
        /// Shrinks only the page presenter. NavigationView.FrameMargin cannot be used: WPF-UI applies it to the container
        /// that also holds ContentOverlay, which would push the dock up by its own height.
        /// </summary>
        private void UpdatePageAreaMargin()
        {
            _pagePresenter ??= FindVisualChild<NavigationViewContentPresenter>(RootNavigation);

            if (_pagePresenter is not null)
                _pagePresenter.Margin = new Thickness(0, 0, 0, BottomDock.ActualHeight);
        }

        private NavigationViewContentPresenter? _pagePresenter;

        private static T? FindVisualChild<T>(DependencyObject root) where T : DependencyObject
        {
            for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); index++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(root, index);

                if (child is T match)
                    return match;

                var nested = FindVisualChild<T>(child);

                if (nested is not null)
                    return nested;
            }

            return null;
        }

        private const double MinOutputHeight = 90;
        private const double MinPageHeight = 220;

        private double _outputHeight = 170;

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.IsOutputVisible))
                UpdateOutputVisibility();
        }

        /// <summary>
        /// Hides the Output tool window (the status bar stays) and restores the height the user dragged it to.
        /// </summary>
        private void UpdateOutputVisibility()
        {
            var visible = _viewModel.IsOutputVisible;

            OutputPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            OutputResizeGrip.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            OutputPanel.Height = _outputHeight;
        }

        private void OnOutputResizeGripDragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
        {
            var maxHeight = Math.Max(MinOutputHeight, RootNavigation.ActualHeight - MinPageHeight);

            _outputHeight = Math.Clamp(_outputHeight - e.VerticalChange, MinOutputHeight, maxHeight);
            OutputPanel.Height = _outputHeight;
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
