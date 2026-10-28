namespace UniversalBackupTool.Interface.Interfaces.Dialogs
{
    public interface IFolderPickerService
    {
        /// <summary>
        /// Shows a folder selection dialog. Returns null when the user cancels.
        /// </summary>
        string? PickFolder(string? initialPath = null);
    }
}