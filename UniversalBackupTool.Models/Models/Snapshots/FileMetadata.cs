namespace UniversalBackupTool.Models;

public record FileMetadata(
    string RelativePath,
    long SizeBytes,
    DateTime LastWriteTimeUtc,
    bool IsSymlink);