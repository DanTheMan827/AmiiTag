using AmiiTag.Core.Interfaces;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace AmiiTag.Core.Services.Web;

/// <summary>
/// Navigation through the Blazor router (web host). Every <see cref="AppRoute"/> maps to an @page template.
/// </summary>
public sealed class BrowserNavigator : IAppNavigator
{
    private readonly NavigationManager _navigation;
    private readonly IJSRuntime _js;

    public BrowserNavigator(NavigationManager navigation, IJSRuntime js)
    {
        _navigation = navigation;
        _js = js;
    }

    public Task NavigateAsync(AppRoute route)
    {
        _navigation.NavigateTo(route.WebPath);
        return Task.CompletedTask;
    }

    public async Task GoBackAsync()
    {
        // Pages opened directly (deep links) have nothing to go back to, so fall back to home.
        var wentBack = await _js.InvokeAsync<bool>("amiitag.goBack");
        if (!wentBack)
        {
            _navigation.NavigateTo("");
        }
    }

    public Task GoHomeAsync()
    {
        _navigation.NavigateTo("");
        return Task.CompletedTask;
    }

    public Task PopToAsync(AppRoute route)
    {
        // The browser shows one screen at a time, so replacing the current history entry is the equivalent.
        _navigation.NavigateTo(route.WebPath, replace: true);
        return Task.CompletedTask;
    }
}
