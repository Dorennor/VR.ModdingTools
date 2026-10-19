using UniversalBackupTool.Model.Models.Snapshots;

namespace UniversalBackupTool.Interfaces.Snapshots
{
    public interface ISnapshotService
    {
        Task<SnapshotData> CreateSnapshotAsync(Guid profileId, string rootPath, CancellationToken cancellationToken = default);

        Task<Dictionary<string, FileMetadata>> GetChangesAsync(string rootPath, SnapshotData baselineSnapshot, CancellationToken cancellationToken = default);

        Task PurgeUnregisteredFilesAsync(string rootPath, SnapshotData baselineSnapshot, CancellationToken cancellationToken = default);
    }
}