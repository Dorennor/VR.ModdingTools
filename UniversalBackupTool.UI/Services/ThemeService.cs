using System.Windows;

using UniversalBackupTool.Interface.Interfaces.Appearance;
using UniversalBackupTool.Model.Models.Settings;

using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace UniversalBackupTool.UI.Services
{
    public sealed class ThemeService : IThemeService
    {
        private Window? _watchedWindow;

        public void Apply(AppTheme theme)
        {
            var window = Application.Current?.MainWindow;

            if (_watchedWindow is not null)
            {
                SystemThemeWatcher.UnWatch(_watchedWindow);
                _watchedWindow = null;
            }

            switch (theme)
            {
                case AppTheme.Light:
                    ApplicationThemeManager.Apply(ApplicationTheme.Light, WindowBackdropType.Mica, true);
                    break;

                case AppTheme.Dark:
                    ApplicationThemeManager.Apply(ApplicationTheme.Dark, WindowBackdropType.Mica, true);
                    break;

                default:
                    ApplicationThemeManager.ApplySystemTheme(true);

                    if (window is not null)
                    {
                        SystemThemeWatcher.Watch(window, WindowBackdropType.Mica, true);
                        _watchedWindow = window;
                    }

                    break;
            }
        }
    }
}
