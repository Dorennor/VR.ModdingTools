namespace UniversalBackupTool.Model.Models.Profiles
{
    public record BackupProfile
    {
        public Guid Id { get; init; } = Guid.NewGuid();

        public string GameName { get; init; } = string.Empty;

        /// <summary>
        /// Whether the game's mods are managed by Vortex. Without Vortex (for example ESO add-ons or Anno 1404) the profile
        /// has no staging folder, and the staging and Vortex AppData components do not apply to it. Profiles saved before
        /// this setting existed load as Vortex profiles.
        /// </summary>
        public bool UsesVortex { get; init; } = true;

        public string? VortexStagingPath { get; init; }

        public string? GameRootPath { get; init; }

        // The Vortex AppData folder is shared by all games and set once in the settings (AppSettings.VortexAppDataPath).
        // profiles.json files written before that still hold a VortexAppDataPath field: the settings migration reads it
        // once, it is ignored on load and dropped on the next save.

        public List<string> CustomFolders { get; init; } = new();

        public bool UseIgnoreList { get; init; } = true;

        public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.Now;
    }
}