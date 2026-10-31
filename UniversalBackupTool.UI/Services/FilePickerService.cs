using System.IO;

using Microsoft.Win32;

using UniversalBackupTool.Interface.Interfaces.Dialogs;

namespace UniversalBackupTool.UI.Services
{
    public sealed class FilePickerService : IFilePickerService
    {
        public IReadOnlyList<string> PickFiles(string? initialDirectory = null)
        {
            var dialog = new OpenFileDialog
            {
                Multiselect = true,
                CheckFileExists = true,
                Title = "Select files to back up"
            };

            if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
                dialog.InitialDirectory = initialDirectory;

            return dialog.ShowDialog() == true ? dialog.FileNames : [];
        }
    }
}
