using System.Collections.ObjectModel;

using UniversalBackupTool.Interface.Interfaces.Logging;
using UniversalBackupTool.Interface.Interfaces.Operations;
using UniversalBackupTool.Model.Models.Operations;

namespace UniversalBackupTool.Service.Services.Operations
{
    public sealed class OperationOutputService : IOperationOutputService
    {
        public const int MaxEntries = 5000;

        private readonly ObservableCollection<OutputEntry> _entries = [];
        private readonly SynchronizationContext? _context;
        private readonly ILogFileWriter? _logFileWriter;

        public OperationOutputService()
            : this(null)
        {
        }

        /// <param name="logFileWriter">Every entry is also appended to the daily log file. Clear only empties the panel.</param>
        public OperationOutputService(ILogFileWriter? logFileWriter)
        {
            // Created on the UI thread by DI, so entries can be added from background threads safely.
            _context = SynchronizationContext.Current;
            _logFileWriter = logFileWriter;
            Entries = new ReadOnlyObservableCollection<OutputEntry>(_entries);
        }

        public ReadOnlyObservableCollection<OutputEntry> Entries { get; }

        public void Write(string message, OutputSeverity severity = OutputSeverity.Info)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            var entry = new OutputEntry(DateTime.Now, severity, message.Trim());

            // Logged immediately on the calling thread, so a crash right after still leaves the line on disk.
            _logFileWriter?.Append(entry);
            Dispatch(() => Add(entry));
        }

        public void Clear() => Dispatch(_entries.Clear);

        private void Add(OutputEntry entry)
        {
            _entries.Add(entry);

            while (_entries.Count > MaxEntries)
                _entries.RemoveAt(0);
        }

        private void Dispatch(Action action)
        {
            if (_context is null || SynchronizationContext.Current == _context)
                action();
            else
                _context.Post(_ => action(), null);
        }
    }
}