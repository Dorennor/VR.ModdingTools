namespace UniversalBackupTool.Model.Models.Profiles
{
    public record BackupProfile
    {
        public Guid Id { get; init; } = Guid.NewGuid();

        public string GameName { get; init; } = string.Empty;

        public string? VortexStagingPath { get; init; }

        public string? GameRootPath { get; init; }

        public string? VortexAppDataPath { get; init; }

        public List<string> CustomFolders { get; init; } = new();

        public bool UseIgnoreList { get; init; } = true;

        public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.Now;
    }
}