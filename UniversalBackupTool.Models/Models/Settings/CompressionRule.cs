using System.IO.Compression;

namespace UniversalBackupTool.Model.Models.Settings
{
    public record CompressionRule(long MaxSizeBytes, CompressionLevel CompressionLevel);
}