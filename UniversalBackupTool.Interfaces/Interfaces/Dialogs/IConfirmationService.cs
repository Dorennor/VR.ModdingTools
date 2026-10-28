namespace UniversalBackupTool.Interface.Interfaces.Dialogs
{
    public interface IConfirmationService
    {
        /// <summary>
        /// Asks the user to confirm a dangerous action. Returns true only when confirmed.
        /// </summary>
        bool Confirm(string title, string message);
    }
}