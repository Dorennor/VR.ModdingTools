using UniversalBackupTool.Model.Models.Storage;

namespace UniversalBackupTool.Interfaces.Storage
{
    public interface IAppStoragePathsProvider
    {
        AppStoragePaths GetPaths();
    }
}