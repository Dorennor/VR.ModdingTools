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
    }
}