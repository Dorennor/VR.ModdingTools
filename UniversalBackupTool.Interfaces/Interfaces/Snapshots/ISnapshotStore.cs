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
    }
}