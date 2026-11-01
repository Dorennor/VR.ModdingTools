using UniversalBackupTool.Model.Models.Settings;

namespace UniversalBackupTool.Interface.Interfaces.Appearance
{
    public interface IThemeService
    {
        /// <summary>
        /// Applies the theme to the running application. <see cref="AppTheme.System"/> also follows later Windows changes.
        /// </summary>
        void Apply(AppTheme theme);
    }
}