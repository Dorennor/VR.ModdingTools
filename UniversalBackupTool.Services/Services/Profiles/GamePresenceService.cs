using System.Text.RegularExpressions;

using UniversalBackupTool.Interface.Interfaces.Profiles;
using UniversalBackupTool.Interfaces.Snapshots;
using UniversalBackupTool.Interfaces.Storage;
using UniversalBackupTool.Model.Models.Profiles;

namespace UniversalBackupTool.Service.Services.Profiles
{
    public sealed class GamePresenceService : IGamePresenceService
    {
        /// <summary>
        /// Below this share of the vanilla snapshot's bytes the folder no longer counts as the game. Deliberately generous:
        /// updates move and rename files, while an uninstall leaves next to none of them.
        /// </summary>
        private const double MinimumSnapshotShare = 0.5;

        /// <summary>
        /// Without a snapshot or Steam manifest, a folder needs this much real game data. Small enough for tiny games, large
        /// enough for what uninstalls leave behind. A smaller game counts once it has a vanilla snapshot.
        /// </summary>
        private const long MinimumGameDataBytes = 10L * 1024 * 1024;

        /// <summary>
        /// What stays behind after an uninstall: logs, crash dumps, settings and saves. These never count as game data.
        /// </summary>
        private static readonly HashSet<string> LeftoverExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".log", ".txt", ".dmp", ".mdmp", ".tmp", ".bak", ".old", ".ini", ".cfg", ".json", ".xml", ".csv", ".sav", ".save"
        };

        private static readonly Regex InstallDirPattern = new("\"installdir\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Links are skipped, so nothing outside the folder is counted or visited. A hardlink counts as a normal file.
        private static readonly EnumerationOptions FolderScan = new()
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false
        };

        private readonly ISnapshotStore _snapshotStore;
        private readonly IAppStoragePathsProvider _storagePathsProvider;

        public GamePresenceService(ISnapshotStore snapshotStore, IAppStoragePathsProvider storagePathsProvider)
        {
            _snapshotStore = snapshotStore ?? throw new ArgumentNullException(nameof(snapshotStore));
            _storagePathsProvider = storagePathsProvider ?? throw new ArgumentNullException(nameof(storagePathsProvider));
        }

        public GamePresence CheckFoldersExist(BackupProfile profile)
        {
            ArgumentNullException.ThrowIfNull(profile);

            return CreatePresence(
                profile,
                GetGameFolder(profile) is { } game && Directory.Exists(game),
                GetStagingFolder(profile) is { } staging && Directory.Exists(staging));
        }

        public Task<IReadOnlyList<GamePresence>> CheckAsync(IReadOnlyCollection<BackupProfile> profiles)
        {
            ArgumentNullException.ThrowIfNull(profiles);

            return Task.Run<IReadOnlyList<GamePresence>>(async () =>
            {
                var steamLibraries = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                var results = new List<GamePresence>(profiles.Count);

                foreach (var profile in profiles)
                {
                    var staging = GetStagingFolder(profile);

                    results.Add(CreatePresence(
                        profile,
                        await IsGameInstalledAsync(profile, steamLibraries),
                        staging is not null && HasAnyFile(staging)));
                }

                return results;
            });
        }

        /// <summary>
        /// Custom paths only say whether a game is there when it has no game or staging folder set: they are usually saves
        /// and configs in Documents, which stay behind after an uninstall. A profile with no paths at all counts as there.
        /// </summary>
        private static GamePresence CreatePresence(BackupProfile profile, bool hasGameFolder, bool hasStaging)
        {
            var customPaths = profile.CustomFolders.Where(path => !string.IsNullOrWhiteSpace(path)).ToList();
            var hasCustomPaths = customPaths.Any(path => Directory.Exists(path) || File.Exists(path));
            var hasOwnFolders = GetGameFolder(profile) is not null || GetStagingFolder(profile) is not null;

            return new GamePresence(
                profile.Id,
                hasGameFolder || (!hasOwnFolders && (customPaths.Count == 0 || hasCustomPaths)),
                hasGameFolder,
                hasStaging,
                hasCustomPaths);
        }

        private async Task<bool> IsGameInstalledAsync(BackupProfile profile, Dictionary<string, HashSet<string>> steamLibraries)
        {
            if (GetGameFolder(profile) is not { } folder || !HasAnyFile(folder))
                return false;

            if (IsListedBySteam(folder, steamLibraries))
                return true;

            return await HasSnapshotContentAsync(profile, folder) ?? HasGameData(folder);
        }

        /// <summary>
        /// True when the folder lies in a Steam library (steamapps\common\name) and a manifest there names it.
        /// </summary>
        private static bool IsListedBySteam(string folder, Dictionary<string, HashSet<string>> steamLibraries)
        {
            for (var game = new DirectoryInfo(folder); game.Parent is { } common; game = common)
            {
                if (common.Parent is not { } steamApps
                    || !common.Name.Equals("common", StringComparison.OrdinalIgnoreCase)
                    || !steamApps.Name.Equals("steamapps", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!steamLibraries.TryGetValue(steamApps.FullName, out var installDirs))
                {
                    installDirs = ReadSteamInstallDirs(steamApps);
                    steamLibraries[steamApps.FullName] = installDirs;
                }

                return installDirs.Contains(game.Name);
            }

            return false;
        }

        private static HashSet<string> ReadSteamInstallDirs(DirectoryInfo steamApps)
        {
            var installDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                foreach (var manifest in steamApps.EnumerateFiles("appmanifest_*.acf"))
                {
                    var match = InstallDirPattern.Match(File.ReadAllText(manifest.FullName));

                    if (match.Success)
                        installDirs.Add(match.Groups[1].Value);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The manifests are only one of the checks; the next ones decide.
            }

            return installDirs;
        }

        /// <summary>
        /// Whether enough of the vanilla snapshot's bytes are still in place, or null without a usable snapshot. Only looks
        /// at paths, so vanilla files changed by an update or replaced by a mod still count. Leftover deployed mods do not:
        /// they were never part of the snapshot.
        /// </summary>
        private async Task<bool?> HasSnapshotContentAsync(BackupProfile profile, string folder)
        {
            try
            {
                var snapshot = await _snapshotStore.LoadAsync(_storagePathsProvider.GetPaths().SnapshotsDirectory, profile.Id);
                var files = snapshot?.Data.Files.Values.Where(file => !file.IsLink && file.SizeBytes > 0).OrderByDescending(file => file.SizeBytes).ToList();

                if (files is null or { Count: 0 })
                    return null;

                var required = files.Sum(file => file.SizeBytes) * MinimumSnapshotShare;
                long found = 0;

                foreach (var file in files)
                {
                    if (File.Exists(Path.Combine(folder, file.RelativePath)))
                        found += file.SizeBytes;

                    if (found >= required)
                        return true;
                }

                return false;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static bool HasGameData(string folder)
        {
            try
            {
                long total = 0;

                foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", FolderScan))
                {
                    if (!LeftoverExtensions.Contains(file.Extension))
                        total += file.Length;

                    if (total >= MinimumGameDataBytes)
                        return true;
                }

                return false;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // It exists but cannot be read to the end: that must not hide a game.
                return true;
            }
        }

        private static bool HasAnyFile(string folder)
        {
            try
            {
                return Directory.Exists(folder) && new DirectoryInfo(folder).EnumerateFiles("*", FolderScan).Any();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return true;
            }
        }

        private static string? GetGameFolder(BackupProfile profile) => NormalizeFolder(profile.GameRootPath);

        private static string? GetStagingFolder(BackupProfile profile) => profile.UsesVortex ? NormalizeFolder(profile.VortexStagingPath) : null;

        private static string? NormalizeFolder(string? path) =>
            string.IsNullOrWhiteSpace(path) ? null : Path.TrimEndingDirectorySeparator(path.Trim());
    }
}