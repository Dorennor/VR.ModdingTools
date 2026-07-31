namespace VR.ModdingTools.VortexBackup.Models
{
    /// <summary>
    /// Represents a folder that can be selected for backup
    /// </summary>
    public class FolderItem
    {
        public string Name { get; set; } = string.Empty;
        public string FullPath { get; set; } = string.Empty;
        public bool IsSelected { get; set; }
        public bool IsCustom { get; set; }
    }
}