using UniversalBackupTool.Interface.Interfaces.Operations;
using UniversalBackupTool.Model.Models.Backups;
using UniversalBackupTool.Model.Models.Operations;

namespace UniversalBackupTool.Service.Services.Operations
{
    public sealed class OperationCoordinator : IOperationCoordinator
    {
        private readonly object _gate = new();
        private readonly IOperationOutputService _output;

        private OperationScope? _current;

        public OperationCoordinator(IOperationOutputService output)
        {
            _output = output ?? throw new ArgumentNullException(nameof(output));
        }

        public event EventHandler? StateChanged;

        public bool IsOperationRunning
        {
            get
            {
                lock (_gate)
                    return _current is not null;
            }
        }

        public string? CurrentOperationName
        {
            get
            {
                lock (_gate)
                    return _current?.OperationName;
            }
        }

        public BackupProgress? CurrentProgress
        {
            get
            {
                lock (_gate)
                    return _current?.AppliedProgress;
            }
        }

        public bool IsCancellationRequested
        {
            get
            {
                lock (_gate)
                    return _current?.CancellationToken.IsCancellationRequested ?? false;
            }
        }

        public IOperationScope? TryBegin(string operationName)
        {
            if (string.IsNullOrWhiteSpace(operationName))
                throw new ArgumentException("Operation name cannot be empty.", nameof(operationName));

            OperationScope scope;

            lock (_gate)
            {
                if (_current is not null)
                    return null;

                scope = new OperationScope(this, operationName.Trim(), SynchronizationContext.Current);
                _current = scope;
            }

            RaiseStateChanged();
            return scope;
        }

        public void CancelCurrent()
        {
            OperationScope? scope;

            lock (_gate)
                scope = _current;

            if (scope is null || scope.CancellationToken.IsCancellationRequested)
                return;

            _output.Write($"{scope.OperationName}: cancellation requested, finishing the current step...", OutputSeverity.Warning);
            scope.Cancel();
            RaiseStateChanged();
        }

        private void End(OperationScope scope)
        {
            lock (_gate)
            {
                if (!ReferenceEquals(_current, scope))
                    return;

                _current = null;
            }

            scope.Context.Dispatch(RaiseStateChanged);
        }

        private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

        private sealed class OperationScope : IOperationScope
        {
            private readonly OperationCoordinator _owner;
            private readonly CancellationTokenSource _cancellation = new();
            private readonly object _progressGate = new();

            private BackupProgress? _pending;
            private bool _postQueued;
            private string? _lastPhase;
            private int _disposed;

            public OperationScope(OperationCoordinator owner, string operationName, SynchronizationContext? context)
            {
                _owner = owner;
                OperationName = operationName;
                Context = new ContextDispatcher(context);
            }

            public string OperationName { get; }

            public CancellationToken CancellationToken => _cancellation.Token;

            public ContextDispatcher Context { get; }

            public BackupProgress? AppliedProgress { get; private set; }

            public void Report(BackupProgress value)
            {
                ArgumentNullException.ThrowIfNull(value);

                if (Volatile.Read(ref _disposed) != 0)
                    return;

                bool queue;

                lock (_progressGate)
                {
                    if (!string.Equals(_lastPhase, value.Phase, StringComparison.Ordinal))
                    {
                        _lastPhase = value.Phase;
                        _owner._output.Write($"{OperationName}: {value.Phase}");
                    }

                    // Coalesce bursts of reports (restore reports every file) into one UI update.
                    _pending = value;
                    queue = !_postQueued;
                    _postQueued = true;
                }

                if (queue)
                    Context.Dispatch(ApplyPending);
            }

            public void Cancel()
            {
                _cancellation.Cancel();
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                    return;

                // The token source is intentionally not disposed: callers may still observe the token after the scope ends.
                _owner.End(this);
            }

            private void ApplyPending()
            {
                lock (_progressGate)
                {
                    _postQueued = false;

                    if (_pending is null || Volatile.Read(ref _disposed) != 0)
                        return;

                    AppliedProgress = _pending;
                    _pending = null;
                }

                _owner.RaiseStateChanged();
            }
        }

        private sealed class ContextDispatcher
        {
            private readonly SynchronizationContext? _context;

            public ContextDispatcher(SynchronizationContext? context)
            {
                _context = context;
            }

            public void Dispatch(Action action)
            {
                if (_context is null || SynchronizationContext.Current == _context)
                    action();
                else
                    _context.Post(_ => action(), null);
            }
        }
    }
}
