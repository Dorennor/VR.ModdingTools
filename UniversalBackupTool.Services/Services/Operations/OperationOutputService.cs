using System.Collections.ObjectModel;

using UniversalBackupTool.Interface.Interfaces.Operations;
using UniversalBackupTool.Model.Models.Operations;

namespace UniversalBackupTool.Service.Services.Operations
{
    public sealed class OperationOutputService : IOperationOutputService
    {
        public const int MaxEntries = 5000;

        private readonly ObservableCollection<OutputEntry> _entries = [];
        private readonly SynchronizationContext? _context;

        public OperationOutputService()
        {
            // Created on the UI thread by DI, so entries can be added from background threads safely.
            _context = SynchronizationContext.Current;
            Entries = new ReadOnlyObservableCollection<OutputEntry>(_entries);
        }

        public ReadOnlyObservableCollection<OutputEntry> Entries { get; }

        public void Write(string message, OutputSeverity severity = OutputSeverity.Info)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            var entry = new OutputEntry(DateTime.Now, severity, message.Trim());
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
