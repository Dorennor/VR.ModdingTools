using UniversalBackupTool.Model.Models.Operations;

namespace UniversalBackupTool.Interface.Interfaces.Logging
{
    /// <summary>
    /// Persists the Output panel to daily log files in the application's Logs folder. Never throws.
    /// </summary>
    public interface ILogFileWriter
    {
        void Append(OutputEntry entry);

        /// <summary>
        /// Writes an exception with its full details (type, message, stack trace, inner exceptions) to the log file only.
        /// </summary>
        void AppendException(string context, Exception exception);

        /// <summary>
        /// High-volume detail line (for example one per archived file) written to the log file only, never to the Output
        /// panel. Lines get a full date and millisecond timestamp and are written in batches about once per second.
        /// </summary>
        void AppendDetail(string message);

        /// <summary>
        /// Writes any batched detail lines now.
        /// </summary>
        void Flush();

        string LogsDirectory { get; }
    }
}