using System.Windows;

using UniversalBackupTool.Interface.Interfaces.Dialogs;

namespace UniversalBackupTool.UI.Services
{
    public sealed class ConfirmationService : IConfirmationService
    {
        public bool Confirm(string title, string message)
        {
            return MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
        }
    }
}