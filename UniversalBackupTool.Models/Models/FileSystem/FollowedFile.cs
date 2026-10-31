namespace UniversalBackupTool.Model.Models.FileSystem
{
    /// <summary>
    /// A file found by a scan that follows links (backups). Reading <see cref="FullPath"/> returns the real bytes even
    /// when it is (or lies below) a symbolic link or junction.
    /// </summary>
    /// <param name="FullPath">Path as seen from the scanned root (may go through links).</param>
    /// <param name="RelativePath">Path relative to the scanned root, using '/' separators.</param>
    /// <param name="IsLink">True when the file itself is a symbolic link.</param>
    public record FollowedFile(string FullPath, string RelativePath, bool IsLink);

    /// <param name="Warnings">Links that were skipped (broken, loops), meant for the Output panel.</param>
    /// <param name="LinksFollowed">Number of file and directory links whose targets were included.</param>
    public record FollowedScan(List<FollowedFile> Files, List<string> Warnings, int LinksFollowed);
}
