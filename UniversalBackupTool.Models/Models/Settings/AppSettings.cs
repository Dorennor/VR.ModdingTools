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

        /// <summary>
        /// The Backup profile picker hides games that are not on this PC (see IGamePresenceService). Restore and the other
        /// pages always list every profile. Off by default, also for settings files from before it existed.
        /// </summary>
        public bool ShowOnlyInstalledGames { get; init; }

        public string SafetyBackupPath { get; init; } = string.IsNullOrWhiteSpace(SafetyBackupPath)
            ? Path.Combine(AppContext.BaseDirectory, "SafetyBackups")
            : SafetyBackupPath;

        public int MaxPreservedBackupsPerGame { get; init; } = MaxPreservedBackupsPerGame < 1 ? 3 : MaxPreservedBackupsPerGame;

        /// <summary>
        /// The Vortex AppData folder, shared by every game Vortex manages and backed up once per backup run. Settings files
        /// from before defaults version 3 get it from the profiles that had their own AppData path, else the usual folder.
        /// </summary>
        public string VortexAppDataPath { get; init; } = string.Empty;

        /// <summary>
        /// Where Vortex keeps its data unless it was moved: %APPDATA%\Vortex.
        /// </summary>
        public static string DefaultVortexAppDataPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vortex");

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
        /// File types (extensions with a leading dot, lowercase) stored without compression whatever their size: formats
        /// that are compressed already, where Deflate only costs time. Measured on the user's mod archives (compressed
        /// size): .ba2 99.5 %, .swf 101 %, .cache 105 %, .bundle 89 %, .w3speech 92 %; the others are compressed media,
        /// archives and game containers. Not listed on purpose: .dds (68 %) and .bsa (81 %), which do shrink.
        /// </summary>
        public List<string> NoCompressionExtensions { get; init; } = [];

        /// <summary>
        /// When the source files of one backup component of a game (for staging: the whole modpack) are larger than this,
        /// all its files are stored without compression. 0 turns the rule off; the per-file rules apply then.
        /// </summary>
        public long StoreComponentsLargerThanBytes { get; init; }

        /// <summary>
        /// How many archives (one per mod for staging, one per custom path) are written at the same time. Compression runs
        /// on one CPU core per archive, so this is usually the number of cores. Below 1 means the number of cores.
        /// </summary>
        public int ParallelArchives { get; init; }

        /// <summary>
        /// <see cref="ParallelArchives"/>, or the number of CPU cores when it is not set.
        /// </summary>
        public int EffectiveParallelArchives => ParallelArchives >= 1 ? ParallelArchives : DefaultParallelArchives;

        /// <summary>
        /// The number of CPU cores (logical processors) the operating system reports.
        /// </summary>
        public static int DefaultParallelArchives => Math.Max(1, Environment.ProcessorCount);

        public static IReadOnlyList<string> DefaultNoCompressionExtensions => VersionedDefaultNoCompressionExtensions.Select(item => item.Extension).ToList();

        /// <summary>
        /// Like <see cref="VersionedDefaultIgnorePatterns"/>: an existing settings file only receives the extensions newer
        /// than its <see cref="DefaultsVersion"/>, so an extension the user removed is not added back.
        /// </summary>
        public static IReadOnlyList<(string Extension, int SinceVersion)> VersionedDefaultNoCompressionExtensions { get; } =
        [
            // General archives.
            (".7z", 4), (".zip", 4), (".rar", 4), (".gz", 4), (".bz2", 4), (".xz", 4), (".zst", 4), (".lz4", 4), (".cab", 4),

            // Game containers that compress their contents: Bethesda BA2, Unreal .pak/.ucas, Unity bundles, RAGE .rpf,
            // Witcher 3 texture/speech caches.
            (".ba2", 4), (".pak", 4), (".ucas", 4), (".bundle", 4), (".rpf", 4), (".cache", 4), (".w3speech", 4),

            // Audio.
            (".mp3", 4), (".ogg", 4), (".opus", 4), (".wem", 4), (".xwm", 4), (".fuz", 4), (".wma", 4), (".m4a", 4), (".aac", 4), (".flac", 4),

            // Video.
            (".bik", 4), (".bk2", 4), (".usm", 4), (".mp4", 4), (".m4v", 4), (".webm", 4), (".mkv", 4), (".wmv", 4),

            // Images and Flash.
            (".png", 4), (".jpg", 4), (".jpeg", 4), (".webp", 4), (".gif", 4), (".swf", 4)
        ];

        /// <summary>
        /// Whether a file is stored without compression because of its type (see <see cref="NoCompressionExtensions"/>).
        /// </summary>
        public bool IsNoCompressionFile(string path)
        {
            var extension = Path.GetExtension(path);

            if (string.IsNullOrEmpty(extension))
                return false;

            foreach (var entry in NoCompressionExtensions)
            {
                if (string.Equals(NormalizeExtension(entry), extension, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// "ba2", "*.ba2" and ".BA2" all mean ".ba2". Empty for blank input.
        /// </summary>
        public static string NormalizeExtension(string? extension)
        {
            var trimmed = (extension ?? string.Empty).Trim().TrimStart('*').Trim().ToLowerInvariant();

            return trimmed.Length == 0 ? string.Empty : trimmed.StartsWith('.') ? trimmed : "." + trimmed;
        }

        /// <summary>
        /// Bumped whenever existing settings files need an upgrade on load: a new pattern in
        /// <see cref="VersionedDefaultIgnorePatterns"/> (versions 1 and 2), the global <see cref="VortexAppDataPath"/>
        /// that replaced the per-profile AppData paths (version 3), or the compression speed settings
        /// <see cref="NoCompressionExtensions"/> and <see cref="ParallelArchives"/> (version 4).
        /// </summary>
        public const int CurrentDefaultsVersion = 4;

        /// <summary>
        /// The defaults version that introduced <see cref="VortexAppDataPath"/>.
        /// </summary>
        public const int GlobalVortexAppDataVersion = 3;

        /// <summary>
        /// The defaults version that introduced <see cref="ParallelArchives"/>.
        /// </summary>
        public const int ParallelArchivesVersion = 4;

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
            VortexAppDataPath = DefaultVortexAppDataPath,
            NoCompressionExtensions = [.. DefaultNoCompressionExtensions],
            ParallelArchives = DefaultParallelArchives,
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