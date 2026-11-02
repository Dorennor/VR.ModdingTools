using UniversalBackupTool.Model.Models.Backups;

namespace UniversalBackupTool.ViewModel.ViewModels.Restore
{
    /// <summary>
    /// One dated backup folder in the Restore page's list.
    /// </summary>
    public sealed record RestoreBackupItem(string Folder, BackupManifest Manifest)
    {
        public string CreatedText => $"{Manifest.CreatedAt:g}";

        public string PartsText => Manifest.Units.Count == 1 ? "1 part" : $"{Manifest.Units.Count:N0} parts";
    }
}