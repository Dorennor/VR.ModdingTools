namespace UniversalBackupTool.ViewModel.ViewModels.Restore
{
    /// <summary>
    /// One entry of the Restore page's game picker.
    /// </summary>
    /// <param name="DisplayName">What the picker shows.</param>
    /// <param name="GameName">The name the game's backups are stored under.</param>
    public sealed record RestoreGameOption(string DisplayName, string GameName);
}