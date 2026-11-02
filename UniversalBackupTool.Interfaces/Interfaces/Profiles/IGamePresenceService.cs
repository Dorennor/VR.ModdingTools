using UniversalBackupTool.Model.Models.Profiles;

namespace UniversalBackupTool.Interface.Interfaces.Profiles
{
    /// <summary>
    /// Tells which games are on this PC. A game counts as on disk while its game folder or (for Vortex games) its staging
    /// folder is there, because either one still holds something to back up. A folder must not be empty and must have at
    /// least half the size last remembered for it; growing is always fine. Profiles with neither folder are judged by
    /// their custom paths, and a profile with no paths at all always counts as on disk.
    /// </summary>
    public interface IGamePresenceService
    {
        /// <summary>
        /// Instant check that only looks at whether the folders exist, without measuring anything.
        /// </summary>
        bool FoldersExist(BackupProfile profile);

        /// <summary>
        /// The full check. Each folder is measured once per application run, in the background, and existence is checked
        /// on every call. Remembers the sizes of the folders that passed in the given file, which only keeps the folders
        /// of these profiles, so pass all profiles.
        /// </summary>
        Task<IReadOnlyList<GamePresence>> CheckAsync(IReadOnlyCollection<BackupProfile> profiles, string sizesFilePath);

        /// <summary>
        /// Measures the folders of these profiles again and remembers the result as their normal size, for a game that
        /// really did shrink (mods removed, clean reinstall).
        /// </summary>
        Task AcceptCurrentSizesAsync(IReadOnlyCollection<BackupProfile> profiles, string sizesFilePath);
    }
}
