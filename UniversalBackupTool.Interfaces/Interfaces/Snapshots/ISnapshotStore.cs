using UniversalBackupTool.Model.Models.Snapshots;

namespace UniversalBackupTool.Interfaces.Snapshots
{
    public interface ISnapshotStore
    {
        /// <summary>
        /// Loads the baseline snapshot of a profile. Returns null when it does not exist or cannot be read.
        /// </summary>
        Task<StoredSnapshot?> LoadAsync(string snapshotsDirectory, Guid profileId, CancellationToken cancellationToken = default);

        Task SaveAsync(string snapshotsDirectory, SnapshotData snapshot, CancellationToken cancellationToken = default);

        /// <summary>
        /// Writes metadata updates of entries already in the stored baseline (new timestamps of files whose content was
        /// verified unchanged). The baseline keeps its creation time; entries not in the stored baseline are not added.
        /// Does nothing when no baseline is stored.
        /// </summary>
        Task UpdateEntriesAsync(string snapshotsDirectory, Guid profileId, IReadOnlyCollection<FileMetadata> entries, CancellationToken cancellationToken = default);
    }
}