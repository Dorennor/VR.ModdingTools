using System.Diagnostics;
using System.IO;

using VR.ModdingTools.VortexBackup.Models;

namespace VR.ModdingTools.VortexBackup.Services
{
    /// <summary>
    /// Service for folder enumeration and management
    /// </summary>
    public class FolderService
    {
        /// <summary>
        /// Gets all direct subfolders of a given root folder (non-recursive)
        /// </summary>
        public static List<FolderItem> GetSubFolders(string rootPath)
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
        public static List<FolderItem> GetAllSubFolders(List<string> rootFolders, List<string> customFolders)
        {
            var allFolders = new List<FolderItem>();

            // Add subfolders from root folders
            foreach (var rootPath in rootFolders)
            {
                var subfolders = GetSubFolders(rootPath);
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
        public bool IsFolderValid(string folderPath)
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