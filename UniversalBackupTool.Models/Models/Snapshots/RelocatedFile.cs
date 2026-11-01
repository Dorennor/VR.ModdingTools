namespace UniversalBackupTool.Model.Models.Snapshots
{
    /// <summary>
    /// A file at a path the snapshot does not know, whose content is that of a vanilla file.
    /// </summary>
    /// <param name="RelativePath">Where the file is now.</param>
    /// <param name="OriginalPath">The vanilla file it matches.</param>
    /// <param name="OriginalMissing">True when nothing is left at the original path (moved); false when something still is (copied, or the original was replaced).</param>
    public sealed record RelocatedFile(string RelativePath, string OriginalPath, bool OriginalMissing);
}