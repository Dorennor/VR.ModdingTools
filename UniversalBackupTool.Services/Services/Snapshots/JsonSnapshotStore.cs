using System.Text.Json;

using UniversalBackupTool.Interfaces.Snapshots;
using UniversalBackupTool.Model.Models.Snapshots;

namespace UniversalBackupTool.Service.Services.Snapshots
{
    public sealed class JsonSnapshotStore : ISnapshotStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = false,
            PropertyNameCaseInsensitive = true
        };

        public async Task<StoredSnapshot?> LoadAsync(string snapshotsDirectory, Guid profileId, CancellationToken cancellationToken = default)
        {
            var filePath = GetFilePath(snapshotsDirectory, profileId);

            if (!File.Exists(filePath))
                return null;

            SnapshotData? data;

            try
            {
                await using var stream = File.OpenRead(filePath);
                data = await JsonSerializer.DeserializeAsync<SnapshotData>(stream, JsonOptions, cancellationToken);
            }
            catch (JsonException)
            {
                return null;
            }

            if (data?.Files is null)
                return null;

            // Deserialization loses the case-insensitive comparer, so rebuild the dictionary.
            var files = new Dictionary<string, FileMetadata>(data.Files, StringComparer.OrdinalIgnoreCase);

            return new StoredSnapshot(new SnapshotData(profileId, files) { Directories = data.Directories }, File.GetLastWriteTime(filePath));
        }

        public async Task SaveAsync(string snapshotsDirectory, SnapshotData snapshot, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(snapshot);

            var filePath = GetFilePath(snapshotsDirectory, snapshot.ProfileId);

            Directory.CreateDirectory(snapshotsDirectory);

            var tempPath = filePath + ".tmp";

            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, snapshot, JsonOptions, cancellationToken);
            }

            File.Move(tempPath, filePath, overwrite: true);
        }

        public async Task UpdateEntriesAsync(
            string snapshotsDirectory,
            Guid profileId,
            IReadOnlyCollection<FileMetadata> entries,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(entries);

            if (entries.Count == 0)
                return;

            var stored = await LoadAsync(snapshotsDirectory, profileId, cancellationToken);

            if (stored is null)
                return;

            var files = stored.Data.Files;
            var updated = 0;

            foreach (var entry in entries)
            {
                if (!files.ContainsKey(entry.RelativePath))
                    continue;

                files[entry.RelativePath] = entry;
                updated++;
            }

            if (updated == 0)
                return;

            await SaveAsync(snapshotsDirectory, stored.Data, cancellationToken);

            // The file time is the snapshot's creation time shown to the user; an update must not make it look new.
            File.SetLastWriteTime(GetFilePath(snapshotsDirectory, profileId), stored.CreatedAt);
        }

        private static string GetFilePath(string snapshotsDirectory, Guid profileId)
        {
            if (string.IsNullOrWhiteSpace(snapshotsDirectory))
                throw new ArgumentException("Snapshots directory cannot be empty.", nameof(snapshotsDirectory));

            return Path.Combine(snapshotsDirectory, profileId.ToString("D") + ".json");
        }
    }
}