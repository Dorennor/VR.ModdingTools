using System.Text;

using UniversalBackupTool.Interface.Interfaces.Logging;
using UniversalBackupTool.Interfaces.Storage;
using UniversalBackupTool.Model.Models.Operations;

namespace UniversalBackupTool.Service.Services.Logging
{
    /// <summary>
    /// One log file per day (log_yyyy-MM-dd.txt) in the portable Logs folder; files older than
    /// <see cref="RetentionDays"/> are removed the first time something is logged in a session.
    /// Normal entries are written immediately. Detail lines (one per archived file) are buffered and written about once
    /// per second, so a backup of many files does not open the log file for every line.
    /// The file is never kept open, so it can be read while the application runs.
    /// Logging problems are swallowed so they can never break an operation.
    /// </summary>
    public sealed class FileLogWriter : ILogFileWriter, IDisposable
    {
        public const int RetentionDays = 30;

        private const int MaxBufferedCharacters = 256 * 1024;

        private readonly object _gate = new();
        private readonly List<(DateTime Timestamp, string Text)> _pending = [];
        private readonly Timer _flushTimer;

        private int _pendingCharacters;
        private bool _cleanedUp;

        public FileLogWriter(IAppStoragePathsProvider storagePathsProvider)
        {
            ArgumentNullException.ThrowIfNull(storagePathsProvider);
            LogsDirectory = storagePathsProvider.GetPaths().LogsDirectory;
            _flushTimer = new Timer(_ => Flush(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }

        public string LogsDirectory { get; }

        public void Append(OutputEntry entry)
        {
            ArgumentNullException.ThrowIfNull(entry);
            Write(entry.Timestamp, $"[{entry.Timestamp:HH:mm:ss}] {entry.Severity.ToString().ToUpperInvariant(),-7} {entry.Message}", flushNow: true);
        }

        public void AppendException(string context, Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);

            var now = DateTime.Now;
            var text = new StringBuilder()
                .Append('[').Append(now.ToString("HH:mm:ss")).Append("] EXCEPTION ").AppendLine(context)
                .Append(exception)
                .ToString();

            Write(now, text, flushNow: true);
        }

        public void AppendDetail(string message)
        {
            if (string.IsNullOrEmpty(message))
                return;

            var now = DateTime.Now;
            Write(now, $"[{now:yyyy-MM-dd HH:mm:ss.fff}] {message}", flushNow: false);
        }

        public void Flush()
        {
            lock (_gate)
                FlushPending();
        }

        public void Dispose()
        {
            _flushTimer.Dispose();
            Flush();
        }

        public string GetLogPath(DateTime timestamp) => Path.Combine(LogsDirectory, $"log_{timestamp:yyyy-MM-dd}.txt");

        private void Write(DateTime timestamp, string text, bool flushNow)
        {
            lock (_gate)
            {
                _pending.Add((timestamp, text));
                _pendingCharacters += text.Length;

                if (flushNow || _pendingCharacters >= MaxBufferedCharacters)
                    FlushPending();
            }
        }

        /// <summary>
        /// Appends buffered lines in order, grouped by day so lines around midnight land in the right file. Caller holds the lock.
        /// </summary>
        private void FlushPending()
        {
            if (_pending.Count == 0)
                return;

            try
            {
                Directory.CreateDirectory(LogsDirectory);

                if (!_cleanedUp)
                {
                    _cleanedUp = true;
                    DeleteExpiredLogs(DateTime.Now);
                }

                foreach (var day in _pending.GroupBy(line => line.Timestamp.Date))
                {
                    var builder = new StringBuilder();

                    foreach (var line in day)
                        builder.AppendLine(line.Text);

                    File.AppendAllText(GetLogPath(day.Key), builder.ToString(), Encoding.UTF8);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The log is best effort; the Output panel still shows normal messages.
            }
            finally
            {
                _pending.Clear();
                _pendingCharacters = 0;
            }
        }

        private void DeleteExpiredLogs(DateTime now)
        {
            var cutoff = now.Date.AddDays(-RetentionDays);

            foreach (var file in new DirectoryInfo(LogsDirectory).EnumerateFiles("log_*.txt"))
            {
                if (file.LastWriteTime < cutoff)
                {
                    try
                    {
                        file.Delete();
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                    }
                }
            }
        }
    }
}