using System.IO.Compression;

namespace UniversalBackupTool.ViewModel.ViewModels.Settings
{
    public sealed record CompressionLevelOption(CompressionLevel Level, string DisplayName);
}