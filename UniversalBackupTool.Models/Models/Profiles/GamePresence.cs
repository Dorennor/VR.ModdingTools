namespace UniversalBackupTool.Model.Models.Profiles
{
    public enum GamePresenceState
    {
        /// <summary>
        /// The game folder or the staging folder is on disk with a plausible size.
        /// </summary>
        OnDisk,

        /// <summary>
        /// None of the game's folders exist, or they are empty.
        /// </summary>
        Missing,

        /// <summary>
        /// A folder exists but is much smaller than the size remembered for it, as after an uninstall that left files behind.
        /// </summary>
        TooSmall
    }

    public record GamePresence(Guid ProfileId, GamePresenceState State);
}