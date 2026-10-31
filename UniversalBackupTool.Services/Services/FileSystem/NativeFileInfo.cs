using System.Runtime.InteropServices;

using Microsoft.Win32.SafeHandles;

namespace UniversalBackupTool.Services
{
    /// <summary>
    /// Hardlink count and file identity, which .NET does not expose. Opens the file with no access rights
    /// (metadata only), so it also works for files that another process keeps locked.
    /// </summary>
    internal static class NativeFileInfo
    {
        private const uint FileFlagBackupSemantics = 0x02000000;
        private const uint FileFlagOpenReparsePoint = 0x00200000;

        /// <summary>
        /// Returns (1, null) when the information cannot be read, so the file is treated as an ordinary file.
        /// </summary>
        public static (int LinkCount, string? FileId) GetLinkInfo(string path)
        {
            if (!OperatingSystem.IsWindows())
                return (1, null);

            var nativePath = path.Length >= 260 && !path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\" + path : path;

            using var handle = CreateFileW(
                nativePath,
                0,
                FileShare.ReadWrite | FileShare.Delete,
                IntPtr.Zero,
                FileMode.Open,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint,
                IntPtr.Zero);

            if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info))
                return (1, null);

            var fileId = $"{info.VolumeSerialNumber:X8}-{info.FileIndexHigh:X8}{info.FileIndexLow:X8}";
            return ((int)Math.Max(1, info.NumberOfLinks), fileId);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFileW(
            string fileName,
            uint desiredAccess,
            FileShare shareMode,
            IntPtr securityAttributes,
            FileMode creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);
    }
}
