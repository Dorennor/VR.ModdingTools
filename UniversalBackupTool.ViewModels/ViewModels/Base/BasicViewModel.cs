using CommunityToolkit.Mvvm.ComponentModel;

namespace UniversalBackupTool.ViewModel.ViewModels.Base
{
    public abstract partial class BasicViewModel : ObservableObject
    {
        [ObservableProperty]
        private bool _isBusy;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasError))]
        private string? _errorMessage;

        public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

        /// <summary>
        /// Runs an operation while IsBusy is set. Re-entrant calls are ignored and failures end up in ErrorMessage.
        /// </summary>
        protected async Task RunBusyAsync(Func<Task> operation)
        {
            ArgumentNullException.ThrowIfNull(operation);

            if (IsBusy)
                return;

            IsBusy = true;
            ErrorMessage = null;

            try
            {
                await operation();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                ErrorMessage = exception.Message;
            }
            finally
            {
                IsBusy = false;
            }
        }
    }
}