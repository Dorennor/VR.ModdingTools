using System.IO.Compression;

namespace UniversalBackupTool.Model.Models.Settings
{
    public record AppSettings(
        string GoogleDriveBackupPath,
        int SafetyBackupRetentionDays = 14,
        List<CompressionRule>? CompressionRules = null,
        string? SafetyBackupPath = null,
        int MaxPreservedBackupsPerGame = 3)
    {
        public List<CompressionRule> CompressionRules { get; init; } = CompressionRules ?? DefaultCompressionRules();

        public List<string> GlobalIgnorePatterns { get; init; } = [];

        public AppTheme Theme { get; init; } = AppTheme.System;

        public string SafetyBackupPath { get; init; } = string.IsNullOrWhiteSpace(SafetyBackupPath)
            ? Path.Combine(AppContext.BaseDirectory, "SafetyBackups")
            : SafetyBackupPath;

        public int MaxPreservedBackupsPerGame { get; init; } = MaxPreservedBackupsPerGame < 1 ? 3 : MaxPreservedBackupsPerGame;

        /// <summary>
        /// Ignore patterns every installation starts with. The ignore list only filters the Game configs component (the
        /// diff of the game folder against its vanilla snapshot); staging, Vortex AppData and custom paths are always backed
        /// up completely, and Purge never uses the list. Vortex leaves its own files in the game folders it deploys to:
        /// deployment manifests (vortex.deployment.json, vortex.deployment.&lt;mod type&gt;.json) and
        /// __folder_managed_by_vortex markers. They are not mod leftovers or configs worth keeping.
        /// </summary>
        public static IReadOnlyList<string> DefaultIgnorePatterns => VersionedDefaultIgnorePatterns.Select(item => item.Pattern).ToList();

        /// <summary>
        /// Each default with the defaults version that introduced it. An existing settings file only receives the patterns
        /// newer than its <see cref="DefaultsVersion"/>, so a default the user removed earlier is never added back.
        /// </summary>
        public static IReadOnlyList<(string Pattern, int SinceVersion)> VersionedDefaultIgnorePatterns { get; } =
        [
            ("vortex.deployment*.json", 1),
            ("__folder_managed_by_vortex", 2)
        ];

        /// <summary>
        /// Bumped whenever <see cref="VersionedDefaultIgnorePatterns"/> gains a pattern that existing settings files should receive.
        /// </summary>
        public const int CurrentDefaultsVersion = 2;

        /// <summary>
        /// Which defaults this settings file has already received. Files from before this existed read as 0, so the
        /// defaults are added once; a pattern the user removes afterwards is not added again.
        /// </summary>
        public int DefaultsVersion { get; init; }

        public static AppSettings Default(string googleDriveBackupPath) => new(
            GoogleDriveBackupPath: googleDriveBackupPath,
            SafetyBackupRetentionDays: 14,
            CompressionRules: DefaultCompressionRules(),
            SafetyBackupPath: Path.Combine(AppContext.BaseDirectory, "SafetyBackups"),
            MaxPreservedBackupsPerGame: 3)
        {
            GlobalIgnorePatterns = [.. DefaultIgnorePatterns],
            DefaultsVersion = CurrentDefaultsVersion
        };

        public static List<CompressionRule> DefaultCompressionRules()
        {
            return
            [
                new(50L * 1024L * 1024L, CompressionLevel.Optimal),
                new(500L * 1024L * 1024L, CompressionLevel.Fastest),
                new(long.MaxValue, CompressionLevel.NoCompression)
            ];
        }
    }
}