namespace UniversalBackupTool.Model.Models.Backups
{
    /// <summary>
    /// An archive that a full game backup deletes before writing the new one.
    /// </summary>
    public record ExistingFullGameArchive(string GameName, string ArchivePath, long Length);
}
