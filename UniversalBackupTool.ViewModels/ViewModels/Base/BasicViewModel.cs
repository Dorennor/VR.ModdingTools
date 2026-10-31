using CommunityToolkit.Mvvm.ComponentModel;

using UniversalBackupTool.Interface.Interfaces.Operations;
using UniversalBackupTool.Model.Models.Operations;

namespace UniversalBackupTool.ViewModel.ViewModels.Base
{
    public abstract partial class BasicViewModel : ObservableObject
    {
        protected BasicViewModel(IOperationOutputService output, IOperationCoordinator coordinator)
        {
            Output = output ?? throw new ArgumentNullException(nameof(output));
            Coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

            // ViewModels are singletons, so this subscription lives as long as the coordinator.
            Coordinator.StateChanged += (_, _) => OnPropertyChanged(nameof(CanStartOperation));
        }

        protected IOperationOutputService Output { get; }

        protected IOperationCoordinator Coordinator { get; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsIdle))]
        [NotifyPropertyChangedFor(nameof(CanStartOperation))]
        private bool _isBusy;

        /// <summary>
        /// True when this page is not doing anything.
        /// </summary>
        public bool IsIdle => !IsBusy;

        /// <summary>
        /// True when this page is idle and no long-running operation is running anywhere in the application.
        /// </summary>
        public bool CanStartOperation => !IsBusy && !Coordinator.IsOperationRunning;

        protected void ReportInfo(string message) => Output.Write(message, OutputSeverity.Info);

        protected void ReportSuccess(string message) => Output.Write(message, OutputSeverity.Success);

        protected void ReportWarning(string message) => Output.Write(message, OutputSeverity.Warning);

        protected void ReportError(string message) => Output.Write(message, OutputSeverity.Error);

        /// <summary>
        /// Runs a short page action while IsBusy is set. Re-entrant calls are ignored and failures are written to the Output panel.
        /// </summary>
        protected async Task RunBusyAsync(Func<Task> action)
        {
            ArgumentNullException.ThrowIfNull(action);

            if (IsBusy)
                return;

            IsBusy = true;

            try
            {
                await action();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                ReportError(exception.Message);
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>
        /// Runs a long operation through the application-wide coordinator: only one runs at a time, its progress and
        /// cancellation are shown in the Output panel, and it keeps running when the user switches pages.
        /// Validate input and ask for confirmation before calling this.
        /// </summary>
        protected Task RunOperationAsync(string operationName, Func<IOperationScope, Task> operation)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
            ArgumentNullException.ThrowIfNull(operation);

            return RunBusyAsync(async () =>
            {
                using var scope = Coordinator.TryBegin(operationName);

                if (scope is null)
                {
                    ReportError($"{operationName} cannot start while \"{Coordinator.CurrentOperationName}\" is running.");
                    return;
                }

                ReportInfo($"{operationName} started.");

                try
                {
                    await operation(scope);
                }
                catch (OperationCanceledException)
                {
                    ReportWarning($"{operationName} was cancelled.");
                }
                catch (Exception exception)
                {
                    ReportError($"{operationName} failed: {exception.Message}");
                }
            });
        }
    }
}
