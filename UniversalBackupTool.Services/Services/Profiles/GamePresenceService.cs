using System.Text.Json;

using UniversalBackupTool.Interface.Interfaces.Profiles;
using UniversalBackupTool.Model.Models.Profiles;

namespace UniversalBackupTool.Service.Services.Profiles
{
    public sealed class GamePresenceService : IGamePresenceService
    {
        /// <summary>
        /// A folder below this share of its remembered size no longer counts as the game. Deliberately generous: mods come
        /// and go, and purging a Vortex deployment shrinks a game folder a lot.
        /// </summary>
        private const double MinimumSizeRatio = 0.5;

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        // Links are skipped, so nothing outside the folder is counted or visited. A hardlink counts as a normal file.
        private static readonly EnumerationOptions SizeScan = new()
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false
        };

        private readonly object _measuredLock = new();
        private readonly Dictionary<string, Task<long?>> _measured = new(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _rememberedGate = new(1, 1);
        private Dictionary<string, long>? _remembered;

        public bool FoldersExist(BackupProfile profile)
        {
            ArgumentNullException.ThrowIfNull(profile);

            var folders = GetCheckedFolders(profile);

            if (folders.Count > 0)
                return folders.Any(Directory.Exists);

            var customPaths = profile.CustomFolders.Where(path => !string.IsNullOrWhiteSpace(path)).ToList();

            return customPaths.Count == 0 || customPaths.Any(path => Directory.Exists(path) || File.Exists(path));
        }

        public async Task<IReadOnlyList<GamePresence>> CheckAsync(IReadOnlyCollection<BackupProfile> profiles, string sizesFilePath)
        {
            ArgumentNullException.ThrowIfNull(profiles);

            var folders = profiles.SelectMany(GetCheckedFolders).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var sizes = new Dictionary<string, long?>(StringComparer.OrdinalIgnoreCase);

            // A folder that is not there now is not measured, so a disconnected drive is simply measured once it is back.
            foreach (var folder in folders.Where(Directory.Exists))
                sizes[folder] = await GetSizeAsync(folder);

            await _rememberedGate.WaitAsync();

            try
            {
                var remembered = await GetRememberedAsync(sizesFilePath);
                var updated = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                var results = new List<GamePresence>(profiles.Count);

                // Folders that did not pass keep their remembered size: the drive may be back, the game may be reinstalled.
                foreach (var folder in folders)
                {
                    if (remembered.TryGetValue(folder, out var size))
                        updated[folder] = size;
                }

                foreach (var profile in profiles)
                {
                    var checkedFolders = GetCheckedFolders(profile);

                    if (checkedFolders.Count == 0)
                    {
                        results.Add(new GamePresence(profile.Id, FoldersExist(profile) ? GamePresenceState.OnDisk : GamePresenceState.Missing));
                        continue;
                    }

                    var states = checkedFolders.Select(folder => EvaluateFolder(folder, sizes, remembered, updated)).ToList();

                    results.Add(new GamePresence(profile.Id, states.Contains(GamePresenceState.OnDisk)
                        ? GamePresenceState.OnDisk
                        : states.Contains(GamePresenceState.TooSmall) ? GamePresenceState.TooSmall : GamePresenceState.Missing));
                }

                await SaveIfChangedAsync(sizesFilePath, updated);

                return results;
            }
            finally
            {
                _rememberedGate.Release();
            }
        }

        public async Task AcceptCurrentSizesAsync(IReadOnlyCollection<BackupProfile> profiles, string sizesFilePath)
        {
            ArgumentNullException.ThrowIfNull(profiles);

            var folders = profiles.SelectMany(GetCheckedFolders).Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists).ToList();

            lock (_measuredLock)
            {
                foreach (var folder in folders)
                    _measured.Remove(folder);
            }

            var sizes = new Dictionary<string, long?>(StringComparer.OrdinalIgnoreCase);

            foreach (var folder in folders)
                sizes[folder] = await GetSizeAsync(folder);

            await _rememberedGate.WaitAsync();

            try
            {
                var updated = new Dictionary<string, long>(await GetRememberedAsync(sizesFilePath), StringComparer.OrdinalIgnoreCase);

                foreach (var (folder, size) in sizes)
                {
                    if (size > 0)
                        updated[folder] = size.Value;
                }

                await SaveIfChangedAsync(sizesFilePath, updated);
            }
            finally
            {
                _rememberedGate.Release();
            }
        }

        private static GamePresenceState EvaluateFolder(
            string folder,
            Dictionary<string, long?> sizes,
            Dictionary<string, long> remembered,
            Dictionary<string, long> updated)
        {
            if (!sizes.TryGetValue(folder, out var size))
                return GamePresenceState.Missing;

            // Could not be measured: it exists, and an unreadable folder must not hide a game.
            if (size is null)
                return GamePresenceState.OnDisk;

            if (size == 0)
                return GamePresenceState.Missing;

            if (remembered.TryGetValue(folder, out var rememberedSize) && size < rememberedSize * MinimumSizeRatio)
                return GamePresenceState.TooSmall;

            updated[folder] = size.Value;

            return GamePresenceState.OnDisk;
        }

        /// <summary>
        /// The folders that decide whether the game is there. Custom paths do not count when a game or staging folder is
        /// set: they are usually saves and configs in Documents, which stay behind after an uninstall.
        /// </summary>
        private static List<string> GetCheckedFolders(BackupProfile profile)
        {
            var folders = new List<string>(2);

            if (!string.IsNullOrWhiteSpace(profile.GameRootPath))
                folders.Add(Path.TrimEndingDirectorySeparator(profile.GameRootPath.Trim()));

            if (profile.UsesVortex && !string.IsNullOrWhiteSpace(profile.VortexStagingPath))
                folders.Add(Path.TrimEndingDirectorySeparator(profile.VortexStagingPath.Trim()));

            return folders;
        }

        private Task<long?> GetSizeAsync(string folder)
        {
            lock (_measuredLock)
            {
                if (!_measured.TryGetValue(folder, out var measurement))
                {
                    measurement = Task.Run(() => MeasureFolder(folder));
                    _measured[folder] = measurement;
                }

                return measurement;
            }
        }

        private static long? MeasureFolder(string folder)
        {
            try
            {
                return new DirectoryInfo(folder).EnumerateFiles("*", SizeScan).Sum(file => file.Length);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        private async Task<Dictionary<string, long>> GetRememberedAsync(string sizesFilePath)
        {
            if (_remembered is not null)
                return _remembered;

            var remembered = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

            if (File.Exists(sizesFilePath))
            {
                try
                {
                    await using var stream = File.OpenRead(sizesFilePath);
                    var stored = await JsonSerializer.DeserializeAsync<Dictionary<string, long>>(stream, JsonOptions);

                    if (stored is not null)
                        remembered = new Dictionary<string, long>(stored, StringComparer.OrdinalIgnoreCase);
                }
                catch (JsonException)
                {
                    // Only remembered sizes are lost: every folder is simply accepted again at its current size.
                }
            }

            _remembered = remembered;

            return remembered;
        }

        private async Task SaveIfChangedAsync(string sizesFilePath, Dictionary<string, long> sizes)
        {
            var remembered = _remembered;

            if (remembered is not null
                && remembered.Count == sizes.Count
                && sizes.All(item => remembered.TryGetValue(item.Key, out var size) && size == item.Value))
                return;

            var directory = Path.GetDirectoryName(sizesFilePath);

            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var tempPath = sizesFilePath + ".tmp";

            await using (var stream = File.Create(tempPath))
                await JsonSerializer.SerializeAsync(stream, sizes, JsonOptions);

            File.Move(tempPath, sizesFilePath, overwrite: true);

            _remembered = sizes;
        }
    }
}
