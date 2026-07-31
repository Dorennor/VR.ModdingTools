using System.IO;
using System.IO.Compression;

namespace VR.ModdingTools.VortexBackup.Services
{
    /// <summary>
    /// Service for compression and backup operations
    /// </summary>
    public class BackupService
    {
        /// <summary>
        /// Compresses selected folders and moves them to the Google Drive backup folder
        /// </summary>
        public async Task<BackupResult> BackupFoldersAsync(List<string> folderPaths, string googleDrivePath, IProgress<string> progress)
        {
            var result = new BackupResult();

            if (!Directory.Exists(googleDrivePath))
            {
                result.HasErrors = true;
                result.ErrorMessage = $"Google Drive path does not exist: {googleDrivePath}";

                return result;
            }

            foreach (var folderPath in folderPaths)
            {
                try
                {
                    if (!Directory.Exists(folderPath))
                    {
                        result.FailedFolders.Add((folderPath, "Folder does not exist"));
                        continue;
                    }

                    var folderInfo = new DirectoryInfo(folderPath);
                    var folderName = folderInfo.Name;
                    var zipFileName = $"{folderName}.zip";
                    var zipFilePath = Path.Combine(googleDrivePath, zipFileName);

                    // Delete existing zip if it exists
                    if (File.Exists(zipFilePath))
                        File.Delete(zipFilePath);

                    progress?.Report($"Compressing {folderName}...");

                    await Task.Run(() =>
                    {
                        ZipFile.CreateFromDirectory(folderPath, zipFilePath, CompressionLevel.Optimal, false);
                    });

                    result.SuccessfulFolders.Add(zipFilePath);
                    progress?.Report($"✓ Completed: {zipFileName}");
                }
                catch (Exception ex)
                {
                    result.HasErrors = true;
                    result.FailedFolders.Add((folderPath, ex.Message));
                    progress?.Report($"✗ Failed {Path.GetFileName(folderPath)}: {ex.Message}");
                }
            }

            return result;
        }
    }

    /// <summary>
    /// Result of backup operation
    /// </summary>
    public class BackupResult
    {
        public List<string> SuccessfulFolders { get; } = new();
        public List<(string FolderPath, string Error)> FailedFolders { get; } = new();
        public bool HasErrors { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
    }
}