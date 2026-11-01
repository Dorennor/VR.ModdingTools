using UniversalBackupTool.Model.Models.Backups;

namespace UniversalBackupTool.Interface.Interfaces.Operations
{
    /// <summary>
    /// Allows only one long-running operation (backup, restore, purge, ...) at a time and exposes its state,
    /// so it keeps running and stays visible regardless of the page that started it.
    /// </summary>
    public interface IOperationCoordinator
    {
        /// <summary>
        /// Raised when an operation starts or ends, reports progress or is cancelled.
        /// </summary>
        event EventHandler? StateChanged;

        bool IsOperationRunning { get; }

        string? CurrentOperationName { get; }

        BackupProgress? CurrentProgress { get; }

        bool IsCancellationRequested { get; }

        /// <summary>
        /// Starts an operation. Returns null when another operation is already running. Dispose the scope to end it.
        /// </summary>
        IOperationScope? TryBegin(string operationName);

        void CancelCurrent();
    }
}