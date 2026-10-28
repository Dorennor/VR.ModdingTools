namespace UniversalBackupTool.Model.Models.Backups
{
    public record BackupOptions(
        bool IncludeStaging = true,
        bool IncludeGameConfigs = true,
        bool IncludeVortexAppData = true,
        bool IncludeCustomFolders = true,
        bool Monolithic = false)
    {
        public bool HasAnyComponent => IncludeStaging || IncludeGameConfigs || IncludeVortexAppData || IncludeCustomFolders;
    }
}