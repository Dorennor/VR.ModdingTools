namespace UniversalBackupTool.Model.Models.FileSystem
{
    /// <summary>
    /// One entry of a scan that does not follow links (game folders: vanilla snapshot, diff, purge).
    /// </summary>
    /// <param name="FullPath">Path of the entry itself (the link, not its target).</param>
    /// <param name="RelativePath">Path relative to the scanned root, using '/' separators.</param>
    /// <param name="Length">File size; 0 for links.</param>
    /// <param name="LinkCount">Number of hardlinks of the file; 1 for ordinary files, 0 for symlinks and directory links.</param>
    /// <param name="FileId">Volume serial + file index, identifying hardlinks of the same file; null when unknown.</param>
    public record ScannedEntry(
        string FullPath,
        string RelativePath,
        FileEntryKind Kind,
        long Length,
        DateTime LastWriteTimeUtc,
        int LinkCount,
        string? FileId);
}