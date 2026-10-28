namespace UniversalBackupTool.Model.Models.Backups
{
    /// <summary>
    /// One restorable part of a backup: the entries of an archive (optionally under a prefix) that belong to one destination.
    /// </summary>
    /// <param name="ArchiveFile">Archive path relative to the backup directory, using '/' separators.</param>
    /// <param name="EntryPrefix">Prefix of the entries inside the archive that belong to this unit (empty or ending with '/').</param>
    /// <param name="OriginalRootPath">Component root at backup time. Used when the current profile has no matching path.</param>
    /// <param name="RelativeDestination">Sub-folder under the component root, for example a mod folder. Empty for the root itself.</param>
    public record BackupManifestUnit(
        BackupComponent Component,
        string ArchiveFile,
        string EntryPrefix,
        string OriginalRootPath,
        string RelativeDestination);
}