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

        /// <summary>
        /// Creation time. A copy or reinstall resets it, so it only serves the first, metadata-only check. Null in snapshots
        /// from before it was recorded, and then not compared.
        /// </summary>
        public DateTime? CreationTimeUtc { get; init; }

        /// <summary>
        /// File version of an executable or library. A different version means a different file, without hashing it.
        /// Null for other files, for files without a version, and in snapshots from before it was recorded.
        /// </summary>
        public string? FileVersion { get; init; }

        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsLink => Kind != FileEntryKind.File;
    }
}