namespace UniversalBackupTool.Model.Models.Profiles
{
    /// <summary>
    /// What is on this PC of a game, as far as a backup is concerned.
    /// </summary>
    /// <param name="ProfileId">The game's profile.</param>
    /// <param name="IsInstalled">The game itself is here. Also true for a profile without a game or staging folder whose custom paths exist, or that has no paths at all.</param>
    /// <param name="HasGameFolder">The game folder holds the installed game, not just what an uninstall left behind.</param>
    /// <param name="HasStaging">The profile uses Vortex and its staging folder holds files.</param>
    /// <param name="HasCustomPaths">At least one of the profile's custom paths exists.</param>
    public record GamePresence(Guid ProfileId, bool IsInstalled, bool HasGameFolder, bool HasStaging, bool HasCustomPaths)
    {
        /// <summary>
        /// Whether "Hide uninstalled games" keeps the game in the list. Mods in staging keep it listed, with the components
        /// it cannot use locked. Custom paths do not: they are usually saves, which stay behind after an uninstall.
        /// </summary>
        public bool IsListed => IsInstalled || HasStaging;

        /// <summary>
        /// Before anything was checked: nothing is hidden or locked.
        /// </summary>
        public static GamePresence Unknown(Guid profileId) => new(profileId, true, true, true, true);
    }
}