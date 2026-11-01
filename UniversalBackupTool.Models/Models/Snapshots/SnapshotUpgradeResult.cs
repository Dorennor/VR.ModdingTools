namespace UniversalBackupTool.Model.Models.Snapshots
{
    /// <summary>
    /// Result of adding missing details to an existing snapshot. Only files proven to be the vanilla file get details;
    /// the others are counted here and left as they are.
    /// </summary>
    /// <param name="Upgraded">Entries that received missing details.</param>
    /// <param name="AlreadyComplete">Entries that needed nothing.</param>
    /// <param name="Missing">Entries whose file is no longer in the game folder.</param>
    /// <param name="Links">Entries where a link is involved (usually a Vortex deployment), whose data says nothing about the vanilla file.</param>
    /// <param name="CannotProve">Entries without a hash whose file has other timestamps, so nothing proves it is still the vanilla file.</param>
    /// <param name="Unreadable">Files that could not be read now (in use, access denied).</param>
    /// <param name="Different">Paths of the files whose size or content differs from the snapshot.</param>
    public sealed record SnapshotUpgradeResult(
        int Upgraded,
        int AlreadyComplete,
        int Missing,
        int Links,
        int CannotProve,
        int Unreadable,
        IReadOnlyList<string> Different);
}