using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

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

        private AppSettings? _loadedSettings;

        public SettingsViewModel(
            ISettingsStore settingsStore,
            IAppStoragePathsProvider storagePathsProvider,
            IFolderPickerService folderPickerService,
            IOperationCoordinator operationCoordinator,
            IOperationOutputService operationOutputService)
            : base(operationOutputService, operationCoordinator)
        {
            _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
            _storagePathsProvider = storagePathsProvider ?? throw new ArgumentNullException(nameof(storagePathsProvider));
            _folderPickerService = folderPickerService ?? throw new ArgumentNullException(nameof(folderPickerService));
        }

        [ObservableProperty]
        private string _googleDriveBackupPath = string.Empty;

        [ObservableProperty]
        private double _safetyBackupRetentionDays = 14;

        [ObservableProperty]
        private string _safetyBackupPath = string.Empty;

        [ObservableProperty]
        private double _maxPreservedBackupsPerGame = 3;

        [ObservableProperty]
        private string _globalIgnorePatternsText = string.Empty;

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
                                        GoogleDriveBackupPath = GoogleDriveBackupPath.Trim(),
                                        SafetyBackupPath = SafetyBackupPath.Trim(),
                                        SafetyBackupRetentionDays = (int)SafetyBackupRetentionDays,
                                        MaxPreservedBackupsPerGame = (int)MaxPreservedBackupsPerGame,
                                        CompressionRules = CompressionRules
                                            .Select(rule => rule.ToModel())
                                            .OrderBy(rule => rule.MaxSizeBytes)
                                            .ToList(),
                                        GlobalIgnorePatterns = ParseIgnorePatterns(GlobalIgnorePatternsText)
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

            GoogleDriveBackupPath = settings.GoogleDriveBackupPath;
            SafetyBackupPath = settings.SafetyBackupPath;
            SafetyBackupRetentionDays = settings.SafetyBackupRetentionDays;
            MaxPreservedBackupsPerGame = settings.MaxPreservedBackupsPerGame;
            GlobalIgnorePatternsText = string.Join(Environment.NewLine, settings.GlobalIgnorePatterns);

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

            if (SafetyBackupRetentionDays < 1 || SafetyBackupRetentionDays != Math.Floor(SafetyBackupRetentionDays))
                return "Safety backup retention must be a whole number of days, at least 1.";

            if (MaxPreservedBackupsPerGame < 1 || MaxPreservedBackupsPerGame != Math.Floor(MaxPreservedBackupsPerGame))
                return "Max preserved backups per game must be a whole number, at least 1.";

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
    }
}