namespace UniversalBackupTool.Model.Models.Backups
{
    public record BackupProgress(
        string Phase,
        string? CurrentItem,
        long BytesProcessed,
        long TotalBytes,
        int FilesProcessed,
        int TotalFiles)
    {
        public double Fraction => TotalBytes > 0
            ? Math.Clamp((double)BytesProcessed / TotalBytes, 0, 1)
            : TotalFiles > 0
                ? Math.Clamp((double)FilesProcessed / TotalFiles, 0, 1)
                : 0;
    }
}