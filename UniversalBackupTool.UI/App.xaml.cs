using System.Windows;

using Microsoft.Extensions.DependencyInjection;

using UniversalBackupTool.Interface.Interfaces.Appearance;
using UniversalBackupTool.Interface.Interfaces.Backups;
using UniversalBackupTool.Interface.Interfaces.Dialogs;
using UniversalBackupTool.Interface.Interfaces.FileSystem;
using UniversalBackupTool.Interface.Interfaces.Jobs;
using UniversalBackupTool.Interface.Interfaces.Logging;
using UniversalBackupTool.Interface.Interfaces.Operations;
using UniversalBackupTool.Interface.Interfaces.Paths;
using UniversalBackupTool.Interface.Interfaces.Profiles;
using UniversalBackupTool.Interface.Interfaces.Settings;
using UniversalBackupTool.Interface.Interfaces.Vortex;
using UniversalBackupTool.Interfaces.Snapshots;
using UniversalBackupTool.Interfaces.Storage;
using UniversalBackupTool.Service.Services.Backups;
using UniversalBackupTool.Service.Services.Jobs;
using UniversalBackupTool.Service.Services.Logging;
using UniversalBackupTool.Service.Services.Operations;
using UniversalBackupTool.Service.Services.Paths;
using UniversalBackupTool.Service.Services.Profiles;
using UniversalBackupTool.Service.Services.Settings;
using UniversalBackupTool.Service.Services.Snapshots;
using UniversalBackupTool.Service.Services.Storage;
using UniversalBackupTool.Service.Services.Vortex;
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

            RegisterErrorHandlers(_serviceProvider);

            _serviceProvider.GetRequiredService<MainWindow>().Show();
        }

        /// <summary>
        /// Unexpected errors are written to the log file with full details and shown in the Output panel instead of
        /// silently closing the application.
        /// </summary>
        private void RegisterErrorHandlers(IServiceProvider provider)
        {
            var log = provider.GetRequiredService<ILogFileWriter>();
            var output = provider.GetRequiredService<IOperationOutputService>();

            DispatcherUnhandledException += (_, args) =>
            {
                log.AppendException("Unhandled UI error", args.Exception);
                output.Write($"Unexpected error: {args.Exception.Message} (details in {log.LogsDirectory})", Model.Models.Operations.OutputSeverity.Error);
                args.Handled = true;
            };

            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                if (args.ExceptionObject is Exception exception)
                    log.AppendException(args.IsTerminating ? "Fatal unhandled error" : "Unhandled error", exception);
            };

            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                log.AppendException("Unobserved background task error", args.Exception);
                args.SetObserved();
            };
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
            services.AddSingleton<IVortexProcessService, VortexProcessService>();
            services.AddSingleton<IOperationCoordinator, OperationCoordinator>();
            services.AddSingleton<ILogFileWriter, FileLogWriter>();
            services.AddSingleton<IOperationOutputService, OperationOutputService>();
            services.AddSingleton<ISafetyBackupService, SafetyBackupService>();
            services.AddSingleton<ISnapshotService, SnapshotService>();
            services.AddSingleton<ISnapshotStore, JsonSnapshotStore>();
            services.AddSingleton<IConfirmationService, ConfirmationService>();
            services.AddSingleton<IArchiveService, ArchiveService>();
            services.AddSingleton<IIgnorePatternMatcher, IgnorePatternMatcher>();
            services.AddSingleton<IBackupJobRunner, BackupJobRunner>();
            services.AddSingleton<IRestoreService, RestoreService>();
            services.AddSingleton<IFolderPickerService, FolderPickerService>();
            services.AddSingleton<IFilePickerService, FilePickerService>();
            services.AddSingleton<IThemeService, ThemeService>();
            services.AddSingleton<INavigationViewPageProvider, ServiceProviderPageProvider>();

            services.AddSingleton<MainViewModel>();
            services.AddSingleton<DashboardViewModel>();
            services.AddSingleton<SettingsViewModel>();
            services.AddSingleton<VanillaPurgeViewModel>();
            services.AddSingleton<BackupRestoreViewModel>();

            services.AddSingleton<MainWindow>();

            services.AddSingleton<DashboardPage>();
            services.AddSingleton<VanillaPurgePage>();
            services.AddSingleton<BackupRestorePage>();
            services.AddSingleton<SettingsPage>();
        }
    }
}