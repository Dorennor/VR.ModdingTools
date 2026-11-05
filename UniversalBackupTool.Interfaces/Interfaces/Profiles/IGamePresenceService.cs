using UniversalBackupTool.Model.Models.Profiles;

namespace UniversalBackupTool.Interface.Interfaces.Profiles
{
    /// <summary>
    /// Tells which games are installed on this PC and what is left to back up of the others. Nothing is remembered between
    /// checks, so a reinstalled game or a reconnected drive counts again on the next check.
    /// </summary>
    public interface IGamePresenceService
    {
        /// <summary>
        /// Instant first answer that only looks at whether the folders exist. <see cref="CheckAsync"/> has the final word.
        /// </summary>
        GamePresence CheckFoldersExist(BackupProfile profile);

        /// <summary>
        /// The full check, off the calling thread. A game folder counts as the installed game when it is not empty and:
        /// Steam lists it in a manifest of its library; else at least half of the vanilla snapshot's bytes are still in
        /// place; else, without a snapshot, it holds real game data rather than only logs and settings. A missing Steam
        /// manifest proves nothing, so such a folder goes on to the next checks.
        /// </summary>
        Task<IReadOnlyList<GamePresence>> CheckAsync(IReadOnlyCollection<BackupProfile> profiles);
    }
}