namespace UniversalBackupTool.Model.Models.FileSystem
{
    /// <summary>
    /// What a directory entry is, as seen without following links.
    /// </summary>
    public enum FileEntryKind
    {
        /// <summary>
        /// An ordinary file with a single name.
        /// </summary>
        File,

        /// <summary>
        /// A file with more than one hardlink (name), for example a mod file Vortex deployed from its staging folder.
        /// Deleting this name leaves the data reachable through the other names.
        /// </summary>
        HardLink,

        /// <summary>
        /// A symbolic link to a file. Deleting it removes only the link.
        /// </summary>
        FileSymlink,

        /// <summary>
        /// A symbolic link or junction to a directory. Deleting it removes only the link; it is never recursed into.
        /// </summary>
        DirectoryLink
    }
}
