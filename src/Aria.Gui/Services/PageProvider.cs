using Aria.Gui.ViewModels;
using Aria.Gui.Views;
using Wpf.Ui.Abstractions;

namespace Aria.Gui.Services;

/// <summary>Crée chaque page une seule fois, avec son ViewModel relié à l'état partagé.</summary>
public sealed class PageProvider(AppState state) : INavigationViewPageProvider
{
    private readonly Dictionary<Type, object> _pages = [];

    public object? GetPage(Type pageType)
    {
        if (_pages.TryGetValue(pageType, out var cached)) return cached;

        object? page = pageType.Name switch
        {
            nameof(DashboardPage) => new DashboardPage(new DashboardViewModel(state)),
            nameof(SettingsPage) => new SettingsPage(new SettingsViewModel(state)),
            nameof(GpuTuningPage) => new GpuTuningPage(new GpuTuningViewModel()),
            nameof(UpdatesPage) => new UpdatesPage(new UpdatesViewModel(state)),
            nameof(CleanupPage) => new CleanupPage(new CleanupViewModel()),
            nameof(RestorePage) => new RestorePage(new RestoreViewModel(state)),
            nameof(ReportsPage) => new ReportsPage(new ReportsViewModel(state)),
            _ => null
        };

        if (page is not null) _pages[pageType] = page;
        return page;
    }
}
