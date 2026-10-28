using UniversalBackupTool.Model.Models.Profiles;

namespace UniversalBackupTool.Interface.Interfaces.Profiles
{
    public interface IProfileStore
    {
        /// <summary>
        /// Loads profiles from the file. Returns an empty list when the file does not exist.
        /// </summary>
        Task<IReadOnlyList<BackupProfile>> LoadAsync(string filePath, CancellationToken cancellationToken = default);

        Task SaveAsync(string filePath, IReadOnlyCollection<BackupProfile> profiles, CancellationToken cancellationToken = default);
    }
}