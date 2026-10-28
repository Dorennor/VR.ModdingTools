namespace UniversalBackupTool.Model.Models.Backups
{
    public record BackupManifest
    {
        public const string FileName = "backup_manifest.json";

        public int Version { get; init; } = 1;

        public Guid ProfileId { get; init; }

        public string GameName { get; init; } = string.Empty;

        public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;

        public bool IsMonolithic { get; init; }

        public List<BackupManifestUnit> Units { get; init; } = [];
    }
}