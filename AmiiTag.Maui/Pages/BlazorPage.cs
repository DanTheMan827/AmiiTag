using AmiiTag.Core.Interfaces;
using AmiiTag.Core.Services;
using AmiiTag.Maui.Services;
using Microsoft.AspNetCore.Components.WebView.Maui;

namespace AmiiTag.Maui.Pages;

/// <summary>
/// A native page whose content is a single shared Blazor component, rendered in its own BlazorWebView.
/// Native navigation (bars, back gestures, sheets) comes from MAUI; the screen itself is the same component the web uses.
/// </summary>
public sealed partial class BlazorPage : ContentPage
{
    public BlazorPage(AppRoute route, SyncedFigureImageFiles figureImages, bool isRoot = false)
    {
        Route = route;
        _chrome = new PageChromeState(DeviceInfo.Platform == DevicePlatform.iOS || DeviceInfo.Platform == DevicePlatform.Android);
        _chrome.Changed += OnChromeChanged;
        Title = route.Title;
        SafeAreaEdges = Microsoft.Maui.SafeAreaEdges.None;

        // Root (home) is black like the Swift app; other screens use the grouped sheet colour.
        if (isRoot)
        {
            BackgroundColor = Colors.Black;
        }
        else
        {
            this.SetAppThemeColor(BackgroundColorProperty, Color.FromArgb("#F2F2F7"), Color.FromArgb("#000000"));
        }

        var webView = new AppBlazorWebView(figureImages)
        {
            HostPage = "wwwroot/index.html",
        };

        webView.RootComponents.Add(new RootComponent
        {
            Selector = "#app",
            ComponentType = typeof(NativePageRoot),
            Parameters = new Dictionary<string, object> { [nameof(NativePageRoot.Route)] = route, [nameof(NativePageRoot.Chrome)] = _chrome },
        });

        webView.BlazorWebViewInitialized += (_, e) => InitializeNativeChrome(e.WebView);
#if ANDROID
        _refresh = new NativePageRefreshView { Content = webView, IsRefreshEnabled = false, RefreshColor = Color.FromArgb("#007AFF") };
        _refresh.Refreshing += async (_, _) => await RefreshFromNative();
        Content = _refresh;
#else
        Content = webView;
#endif
        _settingsItem = new ToolbarItem { Text = "Settings", IconImageSource = "settings_gear.png", Order = ToolbarItemOrder.Primary, Priority = 0 };
        _settingsItem.Clicked += async (_, _) => await _chrome.OpenSettingsAsync();
        SizeChanged += (_, _) => UpdateNativeChrome();
    }

    public AppRoute Route { get; }
    private readonly PageChromeState _chrome;
    private readonly ToolbarItem _settingsItem;
#if ANDROID
    private readonly RefreshView _refresh;
#endif
    partial void InitializeNativeChrome(object webView);
    partial void UpdateNativeChrome();
    partial void ReleaseNativeChrome();
    partial void SuspendNativeChrome();

    private void OnChromeChanged(object? sender, EventArgs e) => Dispatcher.Dispatch(() =>
    {
        if (_closed) return;
        if (_chrome.HasSettings && !ToolbarItems.Contains(_settingsItem)) ToolbarItems.Add(_settingsItem);
        else if (!_chrome.HasSettings) ToolbarItems.Remove(_settingsItem);
#if ANDROID
        _refresh.IsRefreshEnabled = _chrome.CanRefresh;
#endif
        UpdateNativeChrome();
    });

    private async Task RefreshFromNative()
    {
        try { await _chrome.RefreshAsync(); }
        catch (Exception ex)
        {
            var dialogs = IPlatformApplication.Current?.Services.GetService(typeof(IDialogService)) as IDialogService;
            if (dialogs != null) await dialogs.AlertAsync("Couldn't refresh", ex.Message);
        }
        finally
        {
#if ANDROID
            _refresh.IsRefreshing = false;
#endif
            UpdateNativeChrome();
        }
    }

    protected override bool OnBackButtonPressed()
    {
        if (!_chrome.CanGoBack) return base.OnBackButtonPressed();
        Dispatcher.Dispatch(async () =>
        {
            try
            {
                if (!await _chrome.TryGoBackAsync() &&
                    IPlatformApplication.Current?.Services.GetService(typeof(IAppNavigator)) is IAppNavigator navigator)
                    await navigator.GoBackAsync();
            }
            catch (Exception ex)
            {
                var dialogs = IPlatformApplication.Current?.Services.GetService(typeof(IDialogService)) as IDialogService;
                if (dialogs != null) await dialogs.AlertAsync("Couldn't open folder", ex.Message);
            }
        });
        return true;
    }

    private bool IsHome => Route.Component == typeof(AmiiTag.Core.Pages.Home);

    // The main menu watches the NFC reader only while it's in front (not covered by another screen or sheet).
    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (Application.Current != null) Application.Current.RequestedThemeChanged += OnThemeChanged;
        Dispatcher.Dispatch(() => UpdateNativeChrome());
        // Again once the push/pop animation has finished and the navigation bar has settled for this page.
        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(400), () => { if (!_closed) UpdateNativeChrome(); });
        if (IsHome)
        {
            HomeState?.SetVisible(true);
        }
    }

    private void OnThemeChanged(object? sender, AppThemeChangedEventArgs e) => Dispatcher.Dispatch(() => UpdateNativeChrome());

    private bool _closed;

    protected override void OnDisappearing()
    {
        SuspendNativeChrome();
        base.OnDisappearing();
        if (Application.Current != null) Application.Current.RequestedThemeChanged -= OnThemeChanged;
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
                _chrome.Changed -= OnChromeChanged;
                ReleaseNativeChrome();
                ScreenEvents?.NotifyClosed(Route);
            }
        });
    }

    private static AmiiTag.Core.Services.ScreenEvents? ScreenEvents =>
        IPlatformApplication.Current?.Services.GetService(typeof(AmiiTag.Core.Services.ScreenEvents)) as AmiiTag.Core.Services.ScreenEvents;

    private static AmiiTag.Core.Services.HomeScreenState? HomeState =>
        IPlatformApplication.Current?.Services.GetService(typeof(AmiiTag.Core.Services.HomeScreenState)) as AmiiTag.Core.Services.HomeScreenState;
}
