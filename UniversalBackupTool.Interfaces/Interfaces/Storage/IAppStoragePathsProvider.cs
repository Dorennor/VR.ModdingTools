using UniversalBackupTool.Models;

namespace UniversalBackupTool.Interfaces;

public interface IAppStoragePathsProvider
{
    AppStoragePaths GetPaths();
}