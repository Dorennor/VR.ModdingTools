namespace UniversalBackupTool.Interface.Interfaces.Paths
{
    public interface IPathDriveService
    {
        /// <summary>
        /// Returns the drive of a rooted path in the form "D:", or null when the path has no drive letter.
        /// </summary>
        string? GetDrive(string? path);

        /// <summary>
        /// Swaps only the drive letter. New drive accepts "E", "E:" or "E:\".
        /// </summary>
        string ReplaceDrive(string path, string newDrive);

        /// <summary>
        /// Returns the same path on every other available drive where it exists.
        /// </summary>
        IReadOnlyList<string> FindOnOtherDrives(string path);
    }
}