using UniversalBackupTool.Model.Models.Backups;

namespace UniversalBackupTool.Interface.Interfaces.Operations
{
    /// <summary>
    /// A running operation. Disposing it ends the operation.
    /// </summary>
    public interface IOperationScope : IDisposable, IProgress<BackupProgress>
    {
        string OperationName { get; }

        CancellationToken CancellationToken { get; }
    }
}
