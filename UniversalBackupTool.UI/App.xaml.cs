using System.Windows;

using Microsoft.Extensions.DependencyInjection;

using UniversalBackupTool.Interface.Interfaces.Backups;
using UniversalBackupTool.Interface.Interfaces.Dialogs;
using UniversalBackupTool.Interface.Interfaces.FileSystem;
using UniversalBackupTool.Interface.Interfaces.Jobs;
using UniversalBackupTool.Interface.Interfaces.Paths;
using UniversalBackupTool.Interface.Interfaces.Profiles;
using UniversalBackupTool.Interface.Interfaces.Settings;
using UniversalBackupTool.Interfaces.Snapshots;
using UniversalBackupTool.Interfaces.Storage;
using UniversalBackupTool.Service.Services.Backups;
using UniversalBackupTool.Service.Services.Jobs;
using UniversalBackupTool.Service.Services.Paths;
using UniversalBackupTool.Service.Services.Profiles;
using UniversalBackupTool.Service.Services.Settings;
using UniversalBackupTool.Service.Services.Snapshots;
using UniversalBackupTool.Service.Services.Storage;
using UniversalBackupTool.Services;
using UniversalBackupTool.Services.Backups;
using UniversalBackupTool.UI.Services;
using UniversalBackupTool.UI.Views;
using UniversalBackupTool.ViewModel.ViewModels.BackupRestore;
using UniversalBackupTool.ViewModel.ViewModels.Dashboard;
using UniversalBackupTool.ViewModel.ViewModels.Main;
using UniversalBackupTool.ViewModel.ViewModels.Settings;
using UniversalBackupTool.ViewModel.ViewModels.VanillaPurge;

using Wpf.Ui.Abstractions;

namespace UniversalBackupTool.UI
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private ServiceProvider? _serviceProvider;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            var services = new ServiceCollection();
            ConfigureServices(services);

            _serviceProvider = services.BuildServiceProvider();

            _serviceProvider.GetRequiredService<MainWindow>().Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _serviceProvider?.Dispose();

            base.OnExit(e);
        }

        private static void ConfigureServices(IServiceCollection services)
        {
            services.AddSingleton<IAppStoragePathsProvider, AppStoragePathsProvider>();
            services.AddSingleton<IFileSystemProvider, FileSystemProvider>();
            services.AddSingleton<ISettingsStore, JsonSettingsStore>();
            services.AddSingleton<IBackupJobStateService, BackupJobStateService>();
            services.AddSingleton<IProfileStore, JsonProfileStore>();
            services.AddSingleton<ISystemPathsProvider, SystemPathsProvider>();
            services.AddSingleton<IPathDriveService, PathDriveService>();
            services.AddSingleton<ISafetyBackupService, SafetyBackupService>();
            services.AddSingleton<ISnapshotService, SnapshotService>();
            services.AddSingleton<ISnapshotStore, JsonSnapshotStore>();
            services.AddSingleton<IConfirmationService, ConfirmationService>();
            services.AddSingleton<IBackupEngine, BackupEngine>();
            services.AddSingleton<IArchiveService, ArchiveService>();
            services.AddSingleton<IIgnorePatternMatcher, IgnorePatternMatcher>();
            services.AddSingleton<IBackupJobRunner, BackupJobRunner>();
            services.AddSingleton<IRestoreService, RestoreService>();
            services.AddSingleton<IFolderPickerService, FolderPickerService>();
            services.AddSingleton<INavigationViewPageProvider, ServiceProviderPageProvider>();

            services.AddSingleton<MainViewModel>();
            services.AddTransient<DashboardViewModel>();
            services.AddTransient<SettingsViewModel>();
            services.AddTransient<VanillaPurgeViewModel>();
            services.AddTransient<BackupRestoreViewModel>();

            services.AddSingleton<MainWindow>();

            services.AddTransient<DashboardPage>();
            services.AddTransient<VanillaPurgePage>();
            services.AddTransient<BackupRestorePage>();
            services.AddTransient<SettingsPage>();
        }
    }
}