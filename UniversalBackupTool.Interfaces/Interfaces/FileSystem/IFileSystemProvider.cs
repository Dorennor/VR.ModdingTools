using UniversalBackupTool.Model.Models.FileSystem;

namespace UniversalBackupTool.Interface.Interfaces.FileSystem
{
    public interface IFileSystemProvider
    {
        /// <summary>
        /// Lists every file and link below the root without following links (game folders). Directory links are reported
        /// as entries and never recursed into, so nothing outside the root is ever visited.
        /// </summary>
        IReadOnlyList<ScannedEntry> ScanWithoutFollowingLinks(string rootPath, CancellationToken cancellationToken = default);

        /// <summary>
        /// Lists the files to back up below the root, following file and directory links so their real contents are
        /// archived. Broken links, loops and directories already included through another path are skipped and reported.
        /// </summary>
        FollowedScan ScanFollowingLinks(string rootPath, CancellationToken cancellationToken = default);

        /// <summary>
        /// Deletes only the entry itself: for links the link, never its target; directory links are not recursed into.
        /// </summary>
        void DeleteEntry(ScannedEntry entry);
    }
}