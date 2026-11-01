namespace UniversalBackupTool.Model.Models.Profiles
{
    public record BackupProfile
    {
        public Guid Id { get; init; } = Guid.NewGuid();

        public string GameName { get; init; } = string.Empty;

        /// <summary>
        /// Whether the game's mods are managed by Vortex. Without Vortex (for example ESO add-ons or Anno 1404) the profile
        /// has no staging or Vortex AppData folder, and those backup components do not apply to it. Profiles saved before
        /// this setting existed load as Vortex profiles.
        /// </summary>
        public bool UsesVortex { get; init; } = true;

        public string? VortexStagingPath { get; init; }

        public string? GameRootPath { get; init; }

        public string? VortexAppDataPath { get; init; }

        public List<string> CustomFolders { get; init; } = new();

        public bool UseIgnoreList { get; init; } = true;

        public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.Now;
    }
}