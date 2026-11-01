using UniversalBackupTool.Model.Models.FileSystem;

namespace UniversalBackupTool.Model.Models.Snapshots
{
    public record FileMetadata(
        string RelativePath,
        long SizeBytes,
        DateTime LastWriteTimeUtc,
        bool IsSymlink)
    {
        /// <summary>
        /// Entry kind without following links. Snapshots written before link support default to File.
        /// </summary>
        public FileEntryKind Kind { get; init; } = FileEntryKind.File;

        /// <summary>
        /// SHA-256 of the file content (uppercase hex), recorded for regular files when the baseline snapshot is created.
        /// Null for links, for files that could not be read, and in snapshots created before content hashing existed.
        /// </summary>
        public string? Sha256 { get; init; }

        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsLink => Kind != FileEntryKind.File;
    }
}