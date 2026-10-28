using System.IO;

using Microsoft.Win32;

using UniversalBackupTool.Interface.Interfaces.Dialogs;

namespace UniversalBackupTool.UI.Services
{
    public sealed class FolderPickerService : IFolderPickerService
    {
        public string? PickFolder(string? initialPath = null)
        {
            var dialog = new OpenFolderDialog();

            if (!string.IsNullOrWhiteSpace(initialPath) && Directory.Exists(initialPath))
                dialog.InitialDirectory = initialPath;

            return dialog.ShowDialog() == true ? dialog.FolderName : null;
        }
    }
}