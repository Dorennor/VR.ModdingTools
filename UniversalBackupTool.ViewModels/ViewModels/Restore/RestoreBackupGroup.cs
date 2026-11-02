namespace UniversalBackupTool.ViewModel.ViewModels.Restore
{
    /// <summary>
    /// The backups of one component in the Restore page's list, newest first.
    /// </summary>
    public sealed record RestoreBackupGroup(string Title, IReadOnlyList<RestoreBackupItem> Backups);
}