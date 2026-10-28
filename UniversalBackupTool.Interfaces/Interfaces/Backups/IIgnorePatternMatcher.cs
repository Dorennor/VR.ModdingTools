namespace UniversalBackupTool.Interface.Interfaces.Backups
{
    public interface IIgnorePatternMatcher
    {
        /// <summary>
        /// Builds a filter from gitignore-like glob patterns (*, ?, **, trailing / for folders, # comments).
        /// </summary>
        IIgnoreFilter Create(IEnumerable<string> patterns);
    }
}