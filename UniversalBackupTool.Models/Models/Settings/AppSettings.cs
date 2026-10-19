using System.IO.Compression;

namespace UniversalBackupTool.Model.Models.Settings
{
    public record AppSettings(
        string GoogleDriveBackupPath,
        int SafetyBackupRetentionDays = 14,
        List<CompressionRule>? CompressionRules = null)
    {
        public List<CompressionRule> CompressionRules { get; init; } = CompressionRules ?? DefaultCompressionRules();

        public static AppSettings Default(string googleDriveBackupPath) => new(
            GoogleDriveBackupPath: googleDriveBackupPath,
            SafetyBackupRetentionDays: 14,
            CompressionRules: DefaultCompressionRules());

        public static List<CompressionRule> DefaultCompressionRules()
        {
            return
            [
                new(50L * 1024L * 1024L, CompressionLevel.Optimal),
                new(500L * 1024L * 1024L, CompressionLevel.Fastest),
                new(long.MaxValue, CompressionLevel.NoCompression)
            ];
        }
    }
}