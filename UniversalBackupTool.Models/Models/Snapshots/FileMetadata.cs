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

        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsLink => Kind != FileEntryKind.File;
    }
}
