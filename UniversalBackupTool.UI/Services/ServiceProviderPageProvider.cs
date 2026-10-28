using Wpf.Ui.Abstractions;

namespace UniversalBackupTool.UI.Services
{
    /// <summary>
    /// Lets NavigationView create pages through the DI container so they receive their ViewModels.
    /// </summary>
    public sealed class ServiceProviderPageProvider : INavigationViewPageProvider
    {
        private readonly IServiceProvider _serviceProvider;

        public ServiceProviderPageProvider(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        }

        public object? GetPage(Type pageType)
        {
            return _serviceProvider.GetService(pageType);
        }
    }
}