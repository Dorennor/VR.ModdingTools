namespace UniversalBackupTool.Interface.Interfaces.Dialogs
{
    public interface IFilePickerService
    {
        /// <summary>
        /// Shows a file selection dialog allowing several files. Returns an empty list when the user cancels.
        /// </summary>
        IReadOnlyList<string> PickFiles(string? initialDirectory = null);
    }
}
