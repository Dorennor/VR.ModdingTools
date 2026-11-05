using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

using UniversalBackupTool.Interface.Interfaces.Dialogs;
using UniversalBackupTool.UI.Views;

namespace UniversalBackupTool.UI.Services
{
    public sealed class ConfirmationService : IConfirmationService
    {
        private const int SwRestore = 9;

        public bool Confirm(string title, string message)
        {
            // Windows can only be created on the UI thread; a caller may continue on another one after an await.
            return Application.Current.Dispatcher.Invoke(() => ShowDialog(title, message));
        }

        private static bool ShowDialog(string title, string message)
        {
            var dialog = new ConfirmationWindow(title, message);
            var owner = Application.Current.MainWindow;

            if (owner is { IsVisible: true })
            {
                // A window owned by a minimized one stays hidden, and the operation would wait for an unseen question.
                // SW_RESTORE returns to the previous state (also maximized) before the dialog disables the owner.
                if (owner.WindowState == WindowState.Minimized)
                    ShowWindow(new WindowInteropHelper(owner).Handle, SwRestore);

                dialog.Owner = owner;
            }
            else
            {
                dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }

            return dialog.ShowDialog() == true;
        }

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    }
}
