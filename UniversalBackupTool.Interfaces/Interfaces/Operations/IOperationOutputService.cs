using System.Collections.ObjectModel;

using UniversalBackupTool.Model.Models.Operations;

namespace UniversalBackupTool.Interface.Interfaces.Operations
{
    /// <summary>
    /// Application-wide Output panel. Safe to call from any thread: entries are added on the thread that created the service.
    /// </summary>
    public interface IOperationOutputService
    {
        ReadOnlyObservableCollection<OutputEntry> Entries { get; }

        void Write(string message, OutputSeverity severity = OutputSeverity.Info);

        void Clear();
    }
}
