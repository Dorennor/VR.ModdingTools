using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using UniversalBackupTool.Interface.Interfaces.Appearance;
using UniversalBackupTool.Interface.Interfaces.Dialogs;
using UniversalBackupTool.Interface.Interfaces.Operations;
using UniversalBackupTool.Interface.Interfaces.Settings;
using UniversalBackupTool.Interfaces.Storage;
using UniversalBackupTool.Model.Models.Settings;
using UniversalBackupTool.ViewModel.ViewModels.Base;

namespace UniversalBackupTool.ViewModel.ViewModels.Settings
{
    public sealed partial class SettingsViewModel : BasicViewModel
    {
        private const double DefaultNewRuleSizeMegabytes = 100;

        private readonly ISettingsStore _settingsStore;
        private readonly IAppStoragePathsProvider _storagePathsProvider;
        private readonly IFolderPickerService _folderPickerService;
        private readonly IThemeService _themeService;

        private AppSettings? _loadedSettings;
        private bool _isApplyingSettings;

        public SettingsViewModel(
            ISettingsStore settingsStore,
            IAppStoragePathsProvider storagePathsProvider,
            IFolderPickerService folderPickerService,
            IThemeService themeService,
            IOperationCoordinator operationCoordinator,
            IOperationOutputService operationOutputService)
            : base(operationOutputService, operationCoordinator)
        {
            _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
            _storagePathsProvider = storagePathsProvider ?? throw new ArgumentNullException(nameof(storagePathsProvider));
            _folderPickerService = folderPickerService ?? throw new ArgumentNullException(nameof(folderPickerService));
            _themeService = themeService ?? throw new ArgumentNullException(nameof(themeService));
        }

        public IReadOnlyList<ThemeOption> ThemeOptions { get; } =
        [
            new(AppTheme.System, "Use system setting"),
            new(AppTheme.Light, "Light"),
            new(AppTheme.Dark, "Dark")
        ];

        /// <summary>
        /// Applied and saved immediately, like the Windows 11 theme setting; not part of the Save button.
        /// </summary>
        [ObservableProperty]
        private AppTheme _selectedTheme = AppTheme.System;

        partial void OnSelectedThemeChanged(AppTheme value)
        {
            if (_isApplyingSettings)
                return;

            _themeService.Apply(value);
            _ = SaveThemeAsync(value);
        }

        private async Task SaveThemeAsync(AppTheme theme)
        {
            try
            {
                // Merge into the file on disk so unsaved edits on this page are neither saved nor lost.
                var paths = _storagePathsProvider.GetPaths();
                var stored = await _settingsStore.LoadAsync(paths.SettingsFilePath);

                await _settingsStore.SaveAsync(paths.SettingsFilePath, stored with { Theme = theme });

                if (_loadedSettings is not null)
                    _loadedSettings = _loadedSettings with { Theme = theme };
            }
            catch (Exception exception)
            {
                ReportError($"The theme could not be saved: {exception.Message}");
            }
        }

        [ObservableProperty]
        private string _googleDriveBackupPath = string.Empty;

        [ObservableProperty]
        private double _safetyBackupRetentionDays = 14;

        [ObservableProperty]
        private string _safetyBackupPath = string.Empty;

        /// <summary>
        /// The Vortex AppData folder shared by all games Vortex manages; backed up once per backup run.
        /// </summary>
        [ObservableProperty]
        private string _vortexAppDataPath = string.Empty;

        [ObservableProperty]
        private double _maxPreservedBackupsPerGame = 3;

        [ObservableProperty]
        private string _globalIgnorePatternsText = string.Empty;

        /// <summary>
        /// One extension per line; see <see cref="AppSettings.NoCompressionExtensions"/>.
        /// </summary>
        [ObservableProperty]
        private string _noCompressionExtensionsText = string.Empty;

        /// <summary>
        /// <see cref="AppSettings.StoreComponentsLargerThanBytes"/> in GB; 0 turns the rule off.
        /// </summary>
        [ObservableProperty]
        private double _storeComponentsLargerThanGigabytes;

        [ObservableProperty]
        private double _parallelArchives = AppSettings.DefaultParallelArchives;

        public string ParallelArchivesDescription =>
            $"Mods (and custom paths) archived in parallel. Compression uses one CPU core per archive. This PC has {AppSettings.DefaultParallelArchives} logical cores, the default. Lower it if the mods are on a hard disk (HDD).";

        public ObservableCollection<CompressionRuleViewModel> CompressionRules { get; } = [];

        [RelayCommand]
        private Task LoadAsync() => RunBusyAsync(async () =>
                                {
                                    var paths = _storagePathsProvider.GetPaths();
                                    var settings = await _settingsStore.LoadAsync(paths.SettingsFilePath);

                                    Apply(settings);
                                });

        [RelayCommand]
        private Task SaveAsync() => RunBusyAsync(async () =>
                                {
                                    var validationError = Validate();

                                    if (validationError is not null)
                                    {
                                        ReportError(validationError);
                                        return;
                                    }

                                    var baseSettings = _loadedSettings ?? AppSettings.Default(GoogleDriveBackupPath.Trim());

                                    var settings = baseSettings with
                                    {
                                        Theme = SelectedTheme,
                                        GoogleDriveBackupPath = GoogleDriveBackupPath.Trim(),
                                        SafetyBackupPath = SafetyBackupPath.Trim(),
                                        VortexAppDataPath = VortexAppDataPath.Trim(),
                                        SafetyBackupRetentionDays = (int)SafetyBackupRetentionDays,
                                        MaxPreservedBackupsPerGame = (int)MaxPreservedBackupsPerGame,
                                        CompressionRules = CompressionRules
                                            .Select(rule => rule.ToModel())
                                            .OrderBy(rule => rule.MaxSizeBytes)
                                            .ToList(),
                                        GlobalIgnorePatterns = ParseIgnorePatterns(GlobalIgnorePatternsText),
                                        NoCompressionExtensions = ParseExtensions(NoCompressionExtensionsText),
                                        StoreComponentsLargerThanBytes = (long)Math.Round(StoreComponentsLargerThanGigabytes * 1024 * 1024 * 1024),
                                        ParallelArchives = (int)ParallelArchives
                                    };

                                    var paths = _storagePathsProvider.GetPaths();
                                    await _settingsStore.SaveAsync(paths.SettingsFilePath, settings);

                                    Apply(settings);
                                    ReportSuccess("Settings saved.");
                                });

        [RelayCommand]
        private void BrowseGoogleDrivePath()
        {
            var selectedPath = _folderPickerService.PickFolder(GoogleDriveBackupPath);

            if (!string.IsNullOrWhiteSpace(selectedPath))
                GoogleDriveBackupPath = selectedPath;
        }

        [RelayCommand]
        private void BrowseSafetyBackupPath()
        {
            var selectedPath = _folderPickerService.PickFolder(SafetyBackupPath);

            if (!string.IsNullOrWhiteSpace(selectedPath))
                SafetyBackupPath = selectedPath;
        }

        [RelayCommand]
        private void BrowseVortexAppDataPath()
        {
            var selectedPath = _folderPickerService.PickFolder(VortexAppDataPath);

            if (!string.IsNullOrWhiteSpace(selectedPath))
                VortexAppDataPath = selectedPath;
        }

        [RelayCommand]
        private void AddRule()
        {
            var newRule = new CompressionRuleViewModel(new CompressionRule(
                (long)(DefaultNewRuleSizeMegabytes * 1024 * 1024),
                System.IO.Compression.CompressionLevel.Optimal));

            // Keep the "larger files" rule last.
            var unlimitedIndex = CompressionRules
                .Select((rule, index) => (rule, index))
                .FirstOrDefault(item => item.rule.IsUnlimited);

            if (unlimitedIndex.rule is null)
                CompressionRules.Add(newRule);
            else
                CompressionRules.Insert(unlimitedIndex.index, newRule);
        }

        [RelayCommand(CanExecute = nameof(CanRemoveRule))]
        private void RemoveRule(CompressionRuleViewModel? rule)
        {
            if (rule is not null)
                CompressionRules.Remove(rule);
        }

        private static bool CanRemoveRule(CompressionRuleViewModel? rule) => rule is { IsUnlimited: false };

        private void Apply(AppSettings settings)
        {
            _loadedSettings = settings;

            _isApplyingSettings = true;

            try
            {
                SelectedTheme = settings.Theme;
            }
            finally
            {
                _isApplyingSettings = false;
            }

            GoogleDriveBackupPath = settings.GoogleDriveBackupPath;
            SafetyBackupPath = settings.SafetyBackupPath;
            VortexAppDataPath = settings.VortexAppDataPath;
            SafetyBackupRetentionDays = settings.SafetyBackupRetentionDays;
            MaxPreservedBackupsPerGame = settings.MaxPreservedBackupsPerGame;
            GlobalIgnorePatternsText = string.Join(Environment.NewLine, settings.GlobalIgnorePatterns);
            NoCompressionExtensionsText = string.Join(Environment.NewLine, settings.NoCompressionExtensions);
            StoreComponentsLargerThanGigabytes = Math.Round(settings.StoreComponentsLargerThanBytes / (1024.0 * 1024 * 1024), 1);
            ParallelArchives = settings.EffectiveParallelArchives;

            CompressionRules.Clear();

            foreach (var rule in settings.CompressionRules.OrderBy(rule => rule.MaxSizeBytes))
                CompressionRules.Add(new CompressionRuleViewModel(rule));
        }

        private string? Validate()
        {
            if (string.IsNullOrWhiteSpace(GoogleDriveBackupPath))
                return "Google Drive backup path cannot be empty.";

            if (string.IsNullOrWhiteSpace(SafetyBackupPath))
                return "Safety backup path cannot be empty.";

            if (string.IsNullOrWhiteSpace(VortexAppDataPath))
                return "Vortex AppData folder cannot be empty. The usual folder is " + AppSettings.DefaultVortexAppDataPath + ".";

            if (SafetyBackupRetentionDays < 1 || SafetyBackupRetentionDays != Math.Floor(SafetyBackupRetentionDays))
                return "Safety backup retention must be a whole number of days, at least 1.";

            if (MaxPreservedBackupsPerGame < 1 || MaxPreservedBackupsPerGame != Math.Floor(MaxPreservedBackupsPerGame))
                return "Max preserved backups per game must be a whole number, at least 1.";

            if (ParallelArchives < 1 || ParallelArchives > 256 || ParallelArchives != Math.Floor(ParallelArchives))
                return "Archives written at the same time must be a whole number from 1 to 256.";

            if (StoreComponentsLargerThanGigabytes < 0 || double.IsNaN(StoreComponentsLargerThanGigabytes))
                return "The size limit for storing large components without compression must be 0 (off) or more.";

            var limitedRules = CompressionRules.Where(rule => !rule.IsUnlimited).ToList();

            if (limitedRules.Any(rule => rule.MaxSizeMegabytes <= 0))
                return "Compression rule size limits must be greater than 0 MB.";

            if (limitedRules.Select(rule => rule.ToModel().MaxSizeBytes).Distinct().Count() != limitedRules.Count)
                return "Compression rules must have different size limits.";

            return null;
        }

        private static List<string> ParseIgnorePatterns(string text)
        {
            return text
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// One extension per line, also separated by commas or spaces; "ba2", "*.ba2" and ".BA2" are saved as ".ba2".
        /// </summary>
        private static List<string> ParseExtensions(string text)
        {
            return text
                .Split(['\r', '\n', ',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(AppSettings.NormalizeExtension)
                .Where(extension => extension.Length > 1)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}