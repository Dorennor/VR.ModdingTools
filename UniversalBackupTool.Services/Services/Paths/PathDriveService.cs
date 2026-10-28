using UniversalBackupTool.Interface.Interfaces.Paths;

namespace UniversalBackupTool.Service.Services.Paths
{
    public sealed class PathDriveService : IPathDriveService
    {
        private readonly ISystemPathsProvider _systemPathsProvider;

        public PathDriveService(ISystemPathsProvider systemPathsProvider)
        {
            _systemPathsProvider = systemPathsProvider ?? throw new ArgumentNullException(nameof(systemPathsProvider));
        }

        public string? GetDrive(string? path)
        {
            if (string.IsNullOrEmpty(path) || path.Length < 2 || path[1] != ':' || !char.IsAsciiLetter(path[0]))
                return null;

            if (path.Length > 2 && path[2] != '\\' && path[2] != '/')
                return null;

            return char.ToUpperInvariant(path[0]) + ":";
        }

        public string ReplaceDrive(string path, string newDrive)
        {
            if (GetDrive(path) is null)
                throw new ArgumentException("Path does not start with a drive letter.", nameof(path));

            return ParseDrive(newDrive) + path[2..];
        }

        public IReadOnlyList<string> FindOnOtherDrives(string path)
        {
            var currentDrive = GetDrive(path);

            if (currentDrive is null)
                return [];

            var rest = path.Length > 2 ? path[2..] : "\\";
            var found = new List<string>();

            foreach (var drive in _systemPathsProvider.GetAvailableDrives().Select(ParseDrive).Distinct())
            {
                if (string.Equals(drive, currentDrive, StringComparison.OrdinalIgnoreCase))
                    continue;

                var candidate = drive + rest;

                if (_systemPathsProvider.PathExists(candidate))
                    found.Add(candidate);
            }

            return found;
        }

        private static string ParseDrive(string? value)
        {
            var trimmed = value?.Trim().TrimEnd(':', '\\', '/');

            if (trimmed is not { Length: 1 } || !char.IsAsciiLetter(trimmed[0]))
                throw new ArgumentException("Drive must be a single letter, for example D or D:\\.", nameof(value));

            return char.ToUpperInvariant(trimmed[0]) + ":";
        }
    }
}