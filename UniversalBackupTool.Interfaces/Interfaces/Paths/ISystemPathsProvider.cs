namespace UniversalBackupTool.Interface.Interfaces.Paths
{
    public interface ISystemPathsProvider
    {
        /// <summary>
        /// Local (fixed and removable) ready drives in the form "C:", "D:".
        /// </summary>
        IReadOnlyList<string> GetAvailableDrives();

        string GetDefaultVortexAppDataPath();

        bool PathExists(string path);
    }
}