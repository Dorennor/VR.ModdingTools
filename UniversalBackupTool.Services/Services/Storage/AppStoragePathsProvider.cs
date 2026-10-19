using UniversalBackupTool.Interfaces;
using UniversalBackupTool.Models;

namespace UniversalBackupTool.Services;

public sealed class AppStoragePathsProvider : IAppStoragePathsProvider
{
    private readonly AppStoragePaths _paths;

    public AppStoragePathsProvider()  : this(AppContext.BaseDirectory)
    {
    }

    public AppStoragePathsProvider(string baseDirectory)
    {
        _paths = new AppStoragePaths(baseDirectory);
    }

    public AppStoragePaths GetPaths() => _paths;
}