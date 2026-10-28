namespace UniversalBackupTool.Interface.Interfaces.Backups
{
    public interface IIgnoreFilter
    {
        /// <summary>
        /// Returns true when the path (relative to the component root) matches an ignore pattern.
        /// </summary>
        bool IsIgnored(string relativePath);
    }
}