using UniversalBackupTool.Model.Models.Profiles;

namespace UniversalBackupTool.Model.Models.Backups
{
    /// <summary>
    /// The Vortex AppData folder holds the data of every game Vortex manages (one shared LevelDB state database,
    /// extensions, per-game profile files), so it is backed up once per backup run into its own folder next to the game
    /// folders, not once per game. Its backups behave like the backups of a built-in profile with a fixed id and name.
    /// </summary>
    public static class VortexAppDataBackup
    {
        /// <summary>
        /// Folder in the backup destination. The '#' characters keep it apart from any real game name; profiles may not
        /// use this name (see <see cref="IsReservedName"/>).
        /// </summary>
        public const string FolderName = "#Vortex AppData#";

        public const string DisplayName = "Vortex AppData";

        /// <summary>
        /// Profile id written to the manifests of the shared Vortex AppData backups.
        /// </summary>
        public static readonly Guid ProfileId = new("6f1c9a52-3d0b-4e47-9b8e-5a2d7c41e0f3");

        /// <summary>
        /// The built-in profile the shared backup runs as. The folder itself comes from the Vortex AppData path in the settings.
        /// </summary>
        public static BackupProfile CreateProfile() => new()
        {
            Id = ProfileId,
            GameName = FolderName,
            UsesVortex = true,
            UseIgnoreList = false
        };

        public static bool IsSharedBackup(Guid profileId) => profileId == ProfileId;

        /// <summary>
        /// Whether a game name would use the same backup folder as the shared Vortex AppData backups. Windows ignores
        /// case and trailing spaces and dots in folder names, like the backup folder names do.
        /// </summary>
        public static bool IsReservedName(string? gameName)
        {
            if (string.IsNullOrWhiteSpace(gameName))
                return false;

            return string.Equals(gameName.Trim().TrimEnd('.').TrimEnd(), FolderName, StringComparison.OrdinalIgnoreCase);
        }
    }
}