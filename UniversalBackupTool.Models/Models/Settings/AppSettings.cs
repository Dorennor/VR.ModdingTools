using System.IO.Compression;

namespace UniversalBackupTool.Model.Models.Settings
{
    public record AppSettings(
        string GoogleDriveBackupPath,
        int SafetyBackupRetentionDays = 14,
        List<CompressionRule>? CompressionRules = null,
        string? SafetyBackupPath = null,
        int MaxPreservedBackupsPerGame = 3)
    {
        public List<CompressionRule> CompressionRules { get; init; } = CompressionRules ?? DefaultCompressionRules();

        public List<string> GlobalIgnorePatterns { get; init; } = [];

        public AppTheme Theme { get; init; } = AppTheme.System;

        public string SafetyBackupPath { get; init; } = string.IsNullOrWhiteSpace(SafetyBackupPath)
            ? Path.Combine(AppContext.BaseDirectory, "SafetyBackups")
            : SafetyBackupPath;

        public int MaxPreservedBackupsPerGame { get; init; } = MaxPreservedBackupsPerGame < 1 ? 3 : MaxPreservedBackupsPerGame;

        public static AppSettings Default(string googleDriveBackupPath) => new(
            GoogleDriveBackupPath: googleDriveBackupPath,
            SafetyBackupRetentionDays: 14,
            CompressionRules: DefaultCompressionRules(),
            SafetyBackupPath: Path.Combine(AppContext.BaseDirectory, "SafetyBackups"),
            MaxPreservedBackupsPerGame: 3);

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