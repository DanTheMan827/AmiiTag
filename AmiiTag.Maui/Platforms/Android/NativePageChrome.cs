using AmiiTag.Maui.Services;
using Android.OS;
using AndroidX.CoordinatorLayout.Widget;
using Google.Android.Material.AppBar;
using System.Globalization;
using Android.Views;
using AndroidX.Core.View;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;
using AView = Android.Views.View;
using AWebView = Android.Webkit.WebView;

namespace AmiiTag.Maui.Pages;

// BlazorWebView doesn't use MAUI's normal WebViewClient scroll observer.
// Read the actual WebView position, and latch gesture ownership at touch-down.
public sealed class NativePageRefreshView : RefreshView { }

public sealed class NativePageRefreshHandler : RefreshViewHandler
{
    protected override MauiSwipeRefreshLayout CreatePlatformView() => new PageSwipeRefreshLayout(Context);

    private sealed class PageSwipeRefreshLayout(Android.Content.Context context) : MauiSwipeRefreshLayout(context)
    {
        private bool _contentOwnsGesture;
        private static AWebView? FindWebView(AView? view)
        {
            if (view is AWebView webView) return webView;
            if (view is ViewGroup group)
                for (var i = 0; i < group.ChildCount; i++)
                    if (FindWebView(group.GetChildAt(i)) is { } found) return found;
            return null;
        }

        public override bool CanChildScrollUp() => !RefreshEnabled || (FindWebView(this)?.CanScrollVertically(-1) ?? true);

        public override bool OnInterceptTouchEvent(MotionEvent? e)
        {
            if (e?.ActionMasked == MotionEventActions.Down) _contentOwnsGesture = CanChildScrollUp();
            if (_contentOwnsGesture) return false;
            return base.OnInterceptTouchEvent(e);
        }
    }
}

public sealed partial class BlazorPage
{
    private AWebView? _nativeWebView;
    private AppBarLayout? _nativeAppBar;

    partial void InitializeNativeChrome(object webView)
    {
        if (webView is not AWebView web) return;
        _nativeWebView = web;
        web.LayoutChange += OnNativeBoundsChanged;
        UpdateNativeChrome();
    }

    private void OnNativeBoundsChanged(object? sender, EventArgs e) => UpdateNativeChrome();

    partial void UpdateNativeChrome()
    {
        var web = _nativeWebView;
        var window = Platform.CurrentActivity?.Window;
        if (web == null || window?.DecorView is not { } decor || !IsVisible || !ReferenceEquals(MauiNavigator.CurrentPage, this)) return;
        WindowCompat.SetDecorFitsSystemWindows(window, false);
        var dark = Application.Current?.RequestedTheme == AppTheme.Dark;
        decor.SetBackgroundColor(Android.Graphics.Color.ParseColor(dark ? "#0D0F14" : "#F8F9FC"));
#pragma warning disable CA1422, CS0618
        window.SetStatusBarColor(Android.Graphics.Color.Transparent);
        window.SetNavigationBarColor(Android.Graphics.Color.Transparent);
#pragma warning restore CA1422, CS0618
        var controller = WindowCompat.GetInsetsController(window, decor);
        controller.AppearanceLightStatusBars = !dark;
        controller.AppearanceLightNavigationBars = !dark;
        var insets = ViewCompat.GetRootWindowInsets(decor)?.GetInsets(
            WindowInsetsCompat.Type.SystemBars() | WindowInsetsCompat.Type.DisplayCutout());
        var insetTop = insets?.Top ?? 0;
        var insetBottom = insets?.Bottom ?? 0;
        var insetLeft = insets?.Left ?? 0;
        var insetRight = insets?.Right ?? 0;

        // MAUI's app-bar scrolling behavior normally places the fragment below
        // the toolbar. Remove that offset so only CSS pads the HTML controls.
        AView child = web;
        while (child.Parent is AView parent)
        {
            if (parent is CoordinatorLayout coordinator)
            {
                AppBarLayout? appBar = null;
                for (var i = 0; i < coordinator.ChildCount; i++)
                    if (coordinator.GetChildAt(i) is AppBarLayout found) { appBar = found; break; }
                if (appBar != null)
                {
                    coordinator.SetPadding(0, 0, 0, 0);
                    if (child.LayoutParameters is CoordinatorLayout.LayoutParams lp && lp.Behavior != null)
                    {
                        lp.Behavior = null;
                        child.LayoutParameters = lp;
                        child.RequestLayout();
                    }
                    if (!ReferenceEquals(_nativeAppBar, appBar))
                    {
                        ReleaseAppBar();
                        _nativeAppBar = appBar;
                        appBar.LayoutChange += OnNativeBoundsChanged;
                    }
                    appBar.SetPadding(insetLeft, insetTop, insetRight, 0);
                    appBar.SetBackgroundColor(Android.Graphics.Color.Transparent);
                    if (coordinator.IndexOfChild(appBar) != coordinator.ChildCount - 1) appBar.BringToFront();
                    break;
                }
            }
            child = parent;
        }

        var webLocation = new int[2];
        var windowLocation = new int[2];
        web.GetLocationOnScreen(webLocation);
        decor.GetLocationOnScreen(windowLocation);
        var top = Math.Max(0, windowLocation[1] + insetTop - webLocation[1]);
        if (NavigationPage.GetHasNavigationBar(this) && _nativeAppBar is { Height: > 0 } bar)
        {
            var barLocation = new int[2];
            bar.GetLocationOnScreen(barLocation);
            top = Math.Max(top, barLocation[1] + bar.Height - webLocation[1]);
        }
        var bottom = Math.Max(0, webLocation[1] + web.Height - (windowLocation[1] + decor.Height - insetBottom));
        var left = Math.Max(0, windowLocation[0] + insetLeft - webLocation[0]);
        var right = Math.Max(0, webLocation[0] + web.Width - (windowLocation[0] + decor.Width - insetRight));
        var density = web.Resources?.DisplayMetrics?.Density ?? 1;
        string Pixels(int value) => (value / density).ToString("0.##", CultureInfo.InvariantCulture) + "px";
        var barHeight = NavigationPage.GetHasNavigationBar(this) ? Pixels(top) : "0px";
        web.EvaluateJavascript("(() => { const r=document.documentElement; if(!r || !document.querySelector('#app .app-page')) return; r.dataset.nativeAndroid='true'; r.style.setProperty('--native-top','" + Pixels(top) + "'); r.style.setProperty('--native-bar-height','" + barHeight + "'); r.style.setProperty('--safe-bottom','" + Pixels(bottom) + "'); r.style.setProperty('--safe-left','" + Pixels(left) + "'); r.style.setProperty('--safe-right','" + Pixels(right) + "'); })();", null);
    }

    private void ReleaseAppBar()
    {
        if (_nativeAppBar != null)
        {
            _nativeAppBar.LayoutChange -= OnNativeBoundsChanged;
        }
        _nativeAppBar = null;
    }

    partial void ReleaseNativeChrome()
    {
        if (_nativeWebView != null)
        {
            _nativeWebView.LayoutChange -= OnNativeBoundsChanged;
        }
        ReleaseAppBar();
        _nativeWebView = null;
    }
}
