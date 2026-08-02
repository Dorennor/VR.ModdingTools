using System.Diagnostics;
using System.IO;

using VR.ModdingTools.VortexBackup.Models;

namespace VR.ModdingTools.VortexBackup.Helpers
{
    /// <summary>
    /// Service for folder enumeration and management
    /// </summary>
    public static class FolderHelper
    {
        /// <summary>
        /// Gets all direct subfolders of a given root folder (non-recursive)
        /// </summary>
        public static List<FolderItem> GetSubFolders(string rootPath, List<string> ignored = null)
        {
            var subfolders = new List<FolderItem>();

            try
            {
                if (!Directory.Exists(rootPath))
                    return subfolders;

                var directories = Directory.GetDirectories(rootPath);

                foreach (var dirPath in directories)
                {
                    try
                    {
                        var dirInfo = new DirectoryInfo(dirPath);

                        // Skip ignored folder names or paths
                        if (ignored != null && ignored.Count > 0)
                        {
                            var skip = ignored.Any(ignore => !string.IsNullOrWhiteSpace(ignore) &&
                                                              (dirInfo.Name.Equals(ignore, StringComparison.InvariantCultureIgnoreCase) || dirInfo.FullName.Contains(ignore, StringComparison.InvariantCultureIgnoreCase)));

                            if (skip)
                                continue;
                        }

                        subfolders.Add(new FolderItem
                        {
                            Name = dirInfo.Name,
                            FullPath = dirInfo.FullName,
                            IsSelected = false,
                            IsCustom = false
                        });
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // Skip folders we don't have access to
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error enumerating subfolders: {ex.Message}");
            }

            return [.. subfolders.OrderBy(f => f.Name)];
        }

        /// <summary>
        /// Gets all subfolders from multiple root folders combined
        /// </summary>
        public static List<FolderItem> GetAllSubFolders(List<string> rootFolders, List<string> customFolders, List<string> ignored = null)
        {
            var allFolders = new List<FolderItem>();

            // Add subfolders from root folders
            foreach (var rootPath in rootFolders)
            {
                var subfolders = GetSubFolders(rootPath, ignored);
                allFolders.AddRange(subfolders);
            }

            // Add custom folders
            foreach (var customPath in customFolders)
            {
                if (Directory.Exists(customPath))
                {
                    try
                    {
                        var dirInfo = new DirectoryInfo(customPath);

                        allFolders.Add(new FolderItem
                        {
                            Name = dirInfo.Name,
                            FullPath = dirInfo.FullName,
                            IsSelected = false,
                            IsCustom = true
                        });
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error adding custom folder: {ex.Message}");
                    }
                }
            }

            // Remove duplicates and sort by name
            return [.. allFolders.GroupBy(f => f.FullPath).Select(g => g.First()).OrderBy(f => f.Name)];
        }

        /// <summary>
        /// Validates if a folder path exists and is accessible
        /// </summary>
        public static bool IsFolderValid(string folderPath)
        {
            try
            {
                return Directory.Exists(folderPath);
            }
            catch
            {
                return false;
            }
        }
    }
}