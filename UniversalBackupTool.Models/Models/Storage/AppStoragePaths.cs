namespace UniversalBackupTool.Model.Models.Storage
{
    public sealed class AppStoragePaths
    {
        public AppStoragePaths(string baseDirectory)
        {
            BaseDirectory = string.IsNullOrWhiteSpace(baseDirectory) ? throw new ArgumentException("Base directory cannot be empty.", nameof(baseDirectory)) : baseDirectory;
        }

        public string BaseDirectory { get; }

        public string SettingsFilePath => Path.Combine(BaseDirectory, "settings.json");

        public string ProfilesFilePath => Path.Combine(BaseDirectory, "profiles.json");

        public string GameSizesFilePath => Path.Combine(BaseDirectory, "game_sizes.json");

        public string LogsDirectory => Path.Combine(BaseDirectory, "Logs");

        public string TempDirectory => Path.Combine(BaseDirectory, "Temp");

        public string SafetyBackupsDirectory => Path.Combine(BaseDirectory, "SafetyBackups");

        public string SnapshotsDirectory => Path.Combine(BaseDirectory, "Snapshots");

        public string ActiveBackupJobFilePath => Path.Combine(TempDirectory, "active_backup_job.json");
    }
}