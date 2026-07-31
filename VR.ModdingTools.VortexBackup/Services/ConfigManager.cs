using System.Diagnostics;
using System.IO;
using System.Text.Json;

using VR.ModdingTools.VortexBackup.Models;

namespace VR.ModdingTools.VortexBackup.Services
{
    /// <summary>
    /// Manages loading and saving application configuration to/from JSON
    /// </summary>
    public class ConfigManager
    {
        private readonly string _configPath;
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        public ConfigManager(string configFileName = "config.json")
        {
            // Store config in application directory (same folder as .exe)
            var appDirectory = AppDomain.CurrentDomain.BaseDirectory;
            _configPath = Path.Combine(appDirectory, configFileName);
        }

        /// <summary>
        /// Loads configuration from JSON file, or creates default if doesn't exist
        /// </summary>
        public AppConfig LoadConfig()
        {
            try
            {
                if (File.Exists(_configPath))
                {
                    var json = File.ReadAllText(_configPath);
                    return JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error loading config: {ex.Message}");
            }

            return new AppConfig();
        }

        /// <summary>
        /// Saves configuration to JSON file
        /// </summary>
        public void SaveConfig(AppConfig config)
        {
            try
            {
                var json = JsonSerializer.Serialize(config, JsonOptions);
                File.WriteAllText(_configPath, json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error saving config: {ex.Message}");
            }
        }
    }
}