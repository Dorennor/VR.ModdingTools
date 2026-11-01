namespace UniversalBackupTool.Model.Models.Operations
{
    /// <summary>
    /// One line of the Output panel.
    /// </summary>
    public record OutputEntry(DateTime Timestamp, OutputSeverity Severity, string Message)
    {
        public string TimeText => Timestamp.ToString("HH:mm:ss");

        public override string ToString() => $"[{TimeText}] {Severity.ToString().ToUpperInvariant()}: {Message}";
    }
}