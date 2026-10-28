using UniversalBackupTool.Interface.Interfaces.Paths;

namespace UniversalBackupTool.Service.Services.Paths
{
    public sealed class SystemPathsProvider : ISystemPathsProvider
    {
        public IReadOnlyList<string> GetAvailableDrives()
        {
            return DriveInfo.GetDrives()
                .Where(IsLocalAndReady)
                .Select(drive => char.ToUpperInvariant(drive.Name[0]) + ":")
                .Distinct()
                .Order()
                .ToList();
        }

        public string GetDefaultVortexAppDataPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vortex");
        }

        public bool PathExists(string path)
        {
            return Directory.Exists(path) || File.Exists(path);
        }

        private static bool IsLocalAndReady(DriveInfo drive)
        {
            try
            {
                return drive.DriveType is DriveType.Fixed or DriveType.Removable && drive.IsReady;
            }
            catch (IOException)
            {
                return false;
            }
        }
    }
}