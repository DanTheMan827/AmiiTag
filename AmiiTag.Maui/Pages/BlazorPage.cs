using AmiiTag.Core.Interfaces;
using Microsoft.AspNetCore.Components.WebView.Maui;

namespace AmiiTag.Maui.Pages;

/// <summary>
/// A native page whose content is a single shared Blazor component, rendered in its own BlazorWebView.
/// Native navigation (bars, back gestures, sheets) comes from MAUI; the screen itself is the same component the web uses.
/// </summary>
public sealed class BlazorPage : ContentPage
{
    public BlazorPage(AppRoute route, bool isRoot = false)
    {
        Route = route;
        Title = route.Title;

        // Root (home) is black like the Swift app; other screens use the grouped sheet colour.
        if (isRoot)
        {
            BackgroundColor = Colors.Black;
        }
        else
        {
            this.SetAppThemeColor(BackgroundColorProperty, Color.FromArgb("#F2F2F7"), Color.FromArgb("#1C1C1E"));
        }

        var webView = new BlazorWebView
        {
            HostPage = "wwwroot/index.html",
        };

        webView.RootComponents.Add(new RootComponent
        {
            Selector = "#app",
            ComponentType = route.Component,
            Parameters = route.Parameters.Count == 0 ? null : route.Parameters.ToDictionary(p => p.Key, p => p.Value),
        });

        Content = webView;
    }

    public AppRoute Route { get; }

    private bool IsHome => Route.Component == typeof(AmiiTag.Core.Pages.Home);

    // The main menu watches the NFC reader only while it's in front (not covered by another screen or sheet).
    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (IsHome)
        {
            HomeState?.SetVisible(true);
        }
    }

    private bool _closed;

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        if (IsHome)
        {
            HomeState?.SetVisible(false);
        }

        // Disappearing also happens when another screen covers this one. Once the navigation has settled, a page that's
        // no longer on any stack was closed (back, swipe down, Close): let its screen know.
        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(400), () =>
        {
            if (!_closed && !AmiiTag.Maui.Services.MauiNavigator.IsOnStack(this))
            {
                _closed = true;
                ScreenEvents?.NotifyClosed(Route);
            }
        });
    }

    private static AmiiTag.Core.Services.ScreenEvents? ScreenEvents =>
        IPlatformApplication.Current?.Services.GetService(typeof(AmiiTag.Core.Services.ScreenEvents)) as AmiiTag.Core.Services.ScreenEvents;

    private static AmiiTag.Core.Services.HomeScreenState? HomeState =>
        IPlatformApplication.Current?.Services.GetService(typeof(AmiiTag.Core.Services.HomeScreenState)) as AmiiTag.Core.Services.HomeScreenState;
}
