namespace UniversalBackupTool.Model.Models.Backups
{
    /// <param name="RootPath">Resolved component root.</param>
    /// <param name="DestinationPath">Folder the unit is extracted into.</param>
    /// <param name="UsesOriginalPath">True when no matching profile path exists and the path stored in the backup is used.</param>
    public record RestorePlanItem(
        BackupManifestUnit Unit,
        string RootPath,
        string DestinationPath,
        bool UsesOriginalPath);
}