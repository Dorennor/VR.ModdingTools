namespace UniversalBackupTool.Model.Models.Backups
{
    public record BackupOptions(
        bool IncludeStaging = true,
        bool IncludeGameConfigs = false,
        bool IncludeVortexAppData = true,
        bool IncludeCustomFolders = false,
        bool Monolithic = false,
        bool IncludeGameChanges = false,
        bool IncludeGameAdditions = false)
    {
        public bool HasAnyComponent => IncludeStaging || IncludeGameConfigs || IncludeGameAdditions || IncludeGameChanges || IncludeVortexAppData || IncludeCustomFolders;
    }
}
