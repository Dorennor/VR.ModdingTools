namespace UniversalBackupTool.Model.Models.Backups
{
    public record BackupOptions(
        bool IncludeStaging = true,
        bool IncludeGameConfigs = false,
        bool IncludeVortexAppData = true,
        bool IncludeCustomFolders = false,
        bool Monolithic = false,
        bool IncludeGameChanges = false,
        bool IncludeGameAdditions = false,
        bool IncludeFullGame = false)
    {
        public bool HasAnyComponent => UsesBackupDestination || IncludeFullGame;

        /// <summary>
        /// Whether anything goes to the backup destination. A full game backup only writes to the profile's export folder.
        /// </summary>
        public bool UsesBackupDestination => IncludeStaging || IncludeGameConfigs || IncludeGameAdditions || IncludeGameChanges || IncludeVortexAppData || IncludeCustomFolders;
    }
}