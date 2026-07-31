using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

using Microsoft.Win32;

using VR.ModdingTools.VortexBackup.Models;
using VR.ModdingTools.VortexBackup.Services;

namespace VR.ModdingTools.VortexBackup
{
    public partial class MainWindow : Window
    {
        private readonly ConfigManager _configManager;
        private readonly FolderService _folderService;
        private readonly BackupService _backupService;

        private readonly ObservableCollection<FolderItem> _allFolders;
        private readonly ObservableCollection<FolderItem> _currentPageFolders;
        private readonly ObservableCollection<string> _rootFoldersList;

        private AppConfig _config;

        private const int ItemsPerPage = 30;
        private int _currentPage = 1;

        public MainWindow()
        {
            InitializeComponent();

            _configManager = new ConfigManager();
            _folderService = new FolderService();
            _backupService = new BackupService();

            _allFolders = new ObservableCollection<FolderItem>();
            _currentPageFolders = new ObservableCollection<FolderItem>();
            _rootFoldersList = new ObservableCollection<string>();

            FoldersListBox.ItemsSource = _currentPageFolders;
            RootFoldersListBox.ItemsSource = _rootFoldersList;

            LoadConfiguration();
        }

        private void LoadConfiguration()
        {
            _config = _configManager.LoadConfig();
            GoogleDrivePathTextBox.Text = _config.GoogleDrivePath;

            UpdateRootFoldersList();
            RefreshFolderList();
        }

        private void UpdateRootFoldersList()
        {
            _rootFoldersList.Clear();

            foreach (var folder in _config.RootFolders)
                _rootFoldersList.Add($"📁 {folder}");

            foreach (var folder in _config.CustomFolders)
                _rootFoldersList.Add($"⭐ {folder}");
        }

        private void RefreshFolderList()
        {
            _allFolders.Clear();

            var folders = FolderService.GetAllSubFolders(_config.RootFolders, _config.CustomFolders);

            foreach (var folder in folders)
                _allFolders.Add(folder);

            _currentPage = 1;
            DisplayCurrentPage();
        }

        private void DisplayCurrentPage()
        {
            _currentPageFolders.Clear();

            if (_allFolders.Count == 0)
            {
                UpdatePageIndicator();
                return;
            }

            var totalPages = (int)Math.Ceiling((double)_allFolders.Count / ItemsPerPage);

            if (_currentPage < 1)
                _currentPage = 1;

            if (_currentPage > totalPages)
                _currentPage = totalPages;

            var startIndex = (_currentPage - 1) * ItemsPerPage;
            var endIndex = Math.Min(startIndex + ItemsPerPage, _allFolders.Count);

            for (var i = startIndex; i < endIndex; i++)
                _currentPageFolders.Add(_allFolders[i]);

            UpdatePageIndicator();
        }

        private void UpdatePageIndicator()
        {
            var totalPages = _allFolders.Count == 0 ? 1 : (int)Math.Ceiling((double)_allFolders.Count / ItemsPerPage);
            PageIndicator.Text = $"Page {_currentPage} of {totalPages}";

            PrevPageButton.IsEnabled = _currentPage > 1;
            NextPageButton.IsEnabled = _currentPage < totalPages;
        }

        private void BrowseGoogleDrive_Click(object sender, RoutedEventArgs eventArgs)
        {
            var openFolderDialog = new OpenFolderDialog();

            if (openFolderDialog.ShowDialog() == true)
            {
                GoogleDrivePathTextBox.Text = openFolderDialog.FolderName;

                _config.GoogleDrivePath = openFolderDialog.FolderName;
                _configManager.SaveConfig(_config);

                UpdateStatus("✓ Google Drive path updated");
            }
        }

        private void AddRootFolder_Click(object sender, RoutedEventArgs eventArgs)
        {
            var openFolderDialog = new OpenFolderDialog();

            if (openFolderDialog.ShowDialog() == true)
            {
                var folderPath = openFolderDialog.FolderName;

                if (!_config.RootFolders.Contains(folderPath))
                {
                    _config.RootFolders.Add(folderPath);
                    _configManager.SaveConfig(_config);

                    UpdateRootFoldersList();
                    RefreshFolderList();

                    UpdateStatus($"✓ Added: {Path.GetFileName(folderPath)}");
                }
            }
        }

        private void AddCustomFolder_Click(object sender, RoutedEventArgs eventArgs)
        {
            var openFolderDialog = new OpenFolderDialog();

            if (openFolderDialog.ShowDialog() == true)
            {
                var folderPath = openFolderDialog.FolderName;

                if (!_config.CustomFolders.Contains(folderPath))
                {
                    _config.CustomFolders.Add(folderPath);
                    _configManager.SaveConfig(_config);

                    UpdateRootFoldersList();
                    RefreshFolderList();

                    UpdateStatus($"✓ Added custom: {Path.GetFileName(folderPath)}");
                }
            }
        }

        private void PrevPage_Click(object sender, RoutedEventArgs eventArgs)
        {
            if (_currentPage > 1)
            {
                _currentPage--;
                DisplayCurrentPage();
            }
        }

        private void NextPage_Click(object sender, RoutedEventArgs eventArgs)
        {
            var totalPages = (int)Math.Ceiling((double)_allFolders.Count / ItemsPerPage);

            if (_currentPage < totalPages)
            {
                _currentPage++;
                DisplayCurrentPage();
            }
        }

        private void SelectAllCheckBox_Click(object sender, RoutedEventArgs eventArgs)
        {
            if (SelectAllCheckBox.IsChecked == true)
            {
                foreach (var folder in _allFolders)
                    folder.IsSelected = true;
            }
            else
            {
                foreach (var folder in _allFolders)
                    folder.IsSelected = false;
            }

            DisplayCurrentPage();
        }

        private async void StartBackup_Click(object sender, RoutedEventArgs eventArgs)
        {
            if (string.IsNullOrEmpty(_config.GoogleDrivePath))
            {
                UpdateStatus("❌ Error: Set Google Drive path first");
                return;
            }

            if (!Directory.Exists(_config.GoogleDrivePath))
            {
                UpdateStatus("❌ Error: Google Drive path doesn't exist");
                return;
            }

            var selectedFolders = _allFolders.Where(f => f.IsSelected).Select(f => f.FullPath).ToList();

            if (selectedFolders.Count == 0)
            {
                UpdateStatus("❌ Error: Select at least one folder");
                return;
            }

            BackupButton.IsEnabled = false;

            UpdateStatus($"⏳ Backing up {selectedFolders.Count} folder(s)...");

            var progress = new Progress<string>(msg => UpdateStatus(msg));

            try
            {
                var result = await _backupService.BackupFoldersAsync(selectedFolders, _config.GoogleDrivePath, progress);

                if (result.HasErrors && result.SuccessfulFolders.Count == 0)
                {
                    UpdateStatus($"❌ Backup failed: {result.ErrorMessage}");
                }
                else if (result.FailedFolders.Count > 0)
                {
                    UpdateStatus($"⚠️ Partial backup: {result.SuccessfulFolders.Count} ok, {result.FailedFolders.Count} failed");
                }
                else
                {
                    UpdateStatus($"✅ Success! {result.SuccessfulFolders.Count} folder(s) backed up to Google Drive");

                    foreach (var folder in _allFolders)
                        folder.IsSelected = false;

                    DisplayCurrentPage();
                }
            }
            catch (Exception ex)
            {
                UpdateStatus($"❌ Error: {ex.Message}");
            }
            finally
            {
                BackupButton.IsEnabled = true;
            }
        }

        private void UpdateStatus(string message)
        {
            StatusTextBlock.Text = message;
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs eventArgs)
        {
            this.WindowState = WindowState.Minimized;
        }

        private void MaximizeButton_Click(object sender, RoutedEventArgs eventArgs)
        {
            if (this.WindowState == WindowState.Maximized)
                this.WindowState = WindowState.Normal;
            else
                this.WindowState = WindowState.Maximized;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs eventArgs)
        {
            this.Close();
        }

        private void CloseButton_MouseEnter(object sender, MouseEventArgs eventArgs)
        {
            if (sender is Button button)
                button.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E81123"));
        }

        private void CloseButton_MouseLeave(object sender, MouseEventArgs eventArgs)
        {
            if (sender is Button button)
                button.Background = Brushes.Transparent;
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs eventArgs)
        {
            if (eventArgs.LeftButton == MouseButtonState.Pressed)
                this.DragMove();
        }
    }
}