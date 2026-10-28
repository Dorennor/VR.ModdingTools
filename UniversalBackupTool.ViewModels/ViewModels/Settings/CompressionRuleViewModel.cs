using System.IO.Compression;

using CommunityToolkit.Mvvm.ComponentModel;

using UniversalBackupTool.Model.Models.Settings;

namespace UniversalBackupTool.ViewModel.ViewModels.Settings
{
    public sealed partial class CompressionRuleViewModel : ObservableObject
    {
        private const long BytesInMegabyte = 1024L * 1024L;

        public static IReadOnlyList<CompressionLevelOption> LevelOptions { get; } =
        [
            new(CompressionLevel.Optimal, "Optimal"),
            new(CompressionLevel.Fastest, "Fastest"),
            new(CompressionLevel.SmallestSize, "Smallest size"),
            new(CompressionLevel.NoCompression, "No Compression")
        ];

        public CompressionRuleViewModel(CompressionRule rule)
        {
            ArgumentNullException.ThrowIfNull(rule);

            IsUnlimited = rule.MaxSizeBytes == long.MaxValue;
            _maxSizeMegabytes = IsUnlimited ? 0 : (double)rule.MaxSizeBytes / BytesInMegabyte;
            _level = rule.CompressionLevel;
        }

        public bool IsUnlimited { get; }

        [ObservableProperty]
        private double _maxSizeMegabytes;

        [ObservableProperty]
        private CompressionLevel _level;

        public IReadOnlyList<CompressionLevelOption> Options => LevelOptions;

        public CompressionRule ToModel()
        {
            var maxSizeBytes = IsUnlimited
                ? long.MaxValue
                : (long)Math.Round(MaxSizeMegabytes * BytesInMegabyte);

            return new CompressionRule(maxSizeBytes, Level);
        }
    }
}