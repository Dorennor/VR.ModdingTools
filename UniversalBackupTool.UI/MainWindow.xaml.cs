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

            FitInitialSizeToScreen();

            Loaded += OnLoaded;
            SizeChanged += (_, e) => UpdateNavigationPane(e.NewSize.Width);
            RootNavigation.SizeChanged += (_, _) => ApplyOutputHeight();
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

        /// <summary>The XAML size is the preferred one; a small or heavily scaled display must still show the whole window.</summary>
        private void FitInitialSizeToScreen()
        {
            var workArea = SystemParameters.WorkArea;

            Width = Math.Min(Width, workArea.Width * MaxInitialScreenShare);
            Height = Math.Min(Height, workArea.Height * MaxInitialScreenShare);
        }

        /// <summary>Below this width the navigation pane shrinks to icons, whose names are then shown as tooltips.</summary>
        private void UpdateNavigationPane(double windowWidth)
        {
            var isOpen = windowWidth >= MinWidthForOpenPane;

            RootNavigation.IsPaneOpen = isOpen;

            foreach (var item in RootNavigation.MenuItems.Cast<object>().Concat(RootNavigation.FooterMenuItems.Cast<object>()).OfType<DependencyObject>())
                System.Windows.Controls.ToolTipService.SetIsEnabled(item, !isOpen);
        }

        private const double MaxInitialScreenShare = 0.95;
        private const double MinWidthForOpenPane = 1100;
        private const double MinOutputHeight = 90;
        private const double MinPageHeight = 220;

        /// <summary>Output resize grip and status bar, which stay around the Output panel.</summary>
        private const double BottomDockChromeHeight = 34;

        /// <summary>The height the user chose; the panel is shown smaller while the window is too short for it.</summary>
        private double _outputHeight = 170;

        private double MaxOutputHeight => Math.Max(MinOutputHeight, RootNavigation.ActualHeight - MinPageHeight - BottomDockChromeHeight);

        private void ApplyOutputHeight()
        {
            OutputPanel.Height = Math.Min(_outputHeight, MaxOutputHeight);
        }

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
            ApplyOutputHeight();
        }

        private void OnOutputResizeGripDragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
        {
            _outputHeight = Math.Clamp(OutputPanel.Height - e.VerticalChange, MinOutputHeight, MaxOutputHeight);
            ApplyOutputHeight();
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

        private void OnOpenLogsFolderClick(object sender, RoutedEventArgs e)
        {
            var folder = _viewModel.LogsDirectory;
            System.IO.Directory.CreateDirectory(folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
    }
}