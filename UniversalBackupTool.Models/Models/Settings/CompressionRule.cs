using System.IO.Compression;

namespace UniversalBackupTool.Models;

public record CompressionRule(long MaxSizeBytes, CompressionLevel CompressionLevel);