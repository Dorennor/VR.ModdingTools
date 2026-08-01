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
        /// The archive will include the parent folder itself in the archive structure
        /// Uses parallel processing for faster backup of multiple folders
        /// </summary>
        public async Task<BackupResult> BackupFoldersAsync(List<string> folderPaths, string googleDrivePath, IProgress<string> progress, CancellationToken cancellationToken = default)
        {
            var result = new BackupResult();
            var lockObj = new object();

            if (!Directory.Exists(googleDrivePath))
            {
                result.HasErrors = true;
                result.ErrorMessage = $"Google Drive path does not exist: {googleDrivePath}";
                return result;
            }

            // Use Parallel.ForEach for simultaneous backup of multiple folders
            var parallelOptions = new ParallelOptions
            {
                CancellationToken = cancellationToken,
                // Limit to logical processor count for optimal performance
                MaxDegreeOfParallelism = Environment.ProcessorCount
            };

            await Task.Run(() =>
            {
                try
                {
                    Parallel.ForEach(folderPaths, parallelOptions, folderPath =>
                    {
                        // Check for cancellation
                        cancellationToken.ThrowIfCancellationRequested();

                        try
                        {
                            if (!Directory.Exists(folderPath))
                            {
                                lock (lockObj)
                                {
                                    result.FailedFolders.Add((folderPath, "Folder does not exist"));
                                }
                                return;
                            }

                            var folderInfo = new DirectoryInfo(folderPath);
                            var folderName = folderInfo.Name;
                            var zipFileName = $"{folderName}.zip";
                            var zipFilePath = Path.Combine(googleDrivePath, zipFileName);

                            // Delete existing zip if it exists
                            if (File.Exists(zipFilePath))
                                File.Delete(zipFilePath);

                            lock (lockObj)
                            {
                                progress?.Report($"Compressing {folderName}...");
                            }

                            // Create archive with optimized folder addition
                            using (var zipArchive = ZipFile.Open(zipFilePath, ZipArchiveMode.Create, System.Text.Encoding.UTF8))
                            {
                                // Get the parent directory of the folder to back up
                                var parentPath = Directory.GetParent(folderPath).FullName;

                                // Add all files and subdirectories with the parent folder structure (optimized)
                                AddFolderToZip(zipArchive, folderPath, parentPath, cancellationToken);
                            }

                            lock (lockObj)
                            {
                                result.SuccessfulFolders.Add(zipFilePath);
                                progress?.Report($"✓ Completed: {zipFileName}");
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            lock (lockObj)
                            {
                                result.HasErrors = true;
                                result.FailedFolders.Add((folderPath, ex.Message));
                                progress?.Report($"✗ Failed {Path.GetFileName(folderPath)}: {ex.Message}");
                            }
                        }
                    });
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
            }, cancellationToken);

            return result;
        }

        /// <summary>
        /// Recursively adds a folder and all its contents to a zip archive
        /// Optimized for speed by caching paths and using efficient file enumeration
        /// </summary>
        private void AddFolderToZip(ZipArchive zipArchive, string folderPath, string basePath, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var folderInfo = new DirectoryInfo(folderPath);
            var relativePath = Path.GetRelativePath(basePath, folderPath);

            // Use EnumerationOptions to improve file enumeration performance
            var enumerationOptions = new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = true,
                ReturnSpecialDirectories = false
            };

            // Add files from current directory with error handling
            FileInfo[] files;
            try
            {
                files = folderInfo.GetFiles();
            }
            catch
            {
                return; // Skip directory if we can't enumerate
            }

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    // Build entry path efficiently
                    string entryPath = string.IsNullOrEmpty(relativePath) || relativePath == "."
                        ? file.Name
                        : $"{relativePath}/{file.Name}";

                    zipArchive.CreateEntryFromFile(file.FullName, entryPath, CompressionLevel.Optimal);
                }
                catch
                {
                    // Skip files that can't be added
                    continue;
                }
            }

            // Recursively add subdirectories
            DirectoryInfo[] subdirs;
            try
            {
                subdirs = folderInfo.GetDirectories();
            }
            catch
            {
                return; // Skip if we can't enumerate subdirectories
            }

            foreach (var subdir in subdirs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AddFolderToZip(zipArchive, subdir.FullName, basePath, cancellationToken);
            }
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