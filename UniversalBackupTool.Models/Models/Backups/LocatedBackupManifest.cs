namespace UniversalBackupTool.Model.Models.Backups
{
    /// <summary>
    /// A backup manifest and the dated backup folder it was read from.
    /// </summary>
    public sealed record LocatedBackupManifest(string Directory, BackupManifest Manifest);
}