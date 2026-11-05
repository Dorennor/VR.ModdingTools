using UniversalBackupTool.Model.Models.Profiles;

namespace UniversalBackupTool.Model.Models.Backups
{
    public record BackupOptions(
        bool IncludeStaging = true,
        bool IncludeGameConfigs = false,
        bool IncludeVortexAppData = true,
        bool IncludeCustomFolders = false,
        bool Monolithic = false,
        bool IncludeGameChanges = false,
        bool IncludeGameAdditions = false,
        bool IncludeFullGame = false)
    {
        /// <summary>
        /// The games that lack something, by profile id: components they cannot use are skipped for them, also in a run with
        /// games that can. This keeps a leftover folder from replacing a good full game archive. Null when nothing is known
        /// to be missing; a game not listed here is not restricted.
        /// </summary>
        public IReadOnlyDictionary<Guid, GamePresence>? RestrictedGames { get; init; }

        public bool HasAnyComponent => UsesBackupDestination || IncludeFullGame;

        /// <summary>
        /// Whether anything goes to the backup destination. A full game backup only writes to the profile's export folder.
        /// </summary>
        public bool UsesBackupDestination => IncludeStaging || IncludeGameConfigs || IncludeGameAdditions || IncludeGameChanges || IncludeVortexAppData || IncludeCustomFolders;
    }
}