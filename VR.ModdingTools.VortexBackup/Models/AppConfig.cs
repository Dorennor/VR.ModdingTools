namespace VR.ModdingTools.VortexBackup.Models
{
    /// <summary>
    /// Application configuration model persisted in JSON
    /// </summary>
    public class AppConfig
    {
        public string GoogleDrivePath { get; set; } = string.Empty;
        public List<string> RootFolders { get; set; } = [];
        public List<string> CustomFolders { get; set; } = [];

        // List of folder names or full paths to ignore when enumerating subfolders
        // By default ignore common cache and downloads folders
        public List<string> IgnoredFolders { get; set; } = ["cache", "downloads"];
    }
}