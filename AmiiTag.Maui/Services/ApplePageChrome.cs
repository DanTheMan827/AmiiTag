#if IOS || MACCATALYST
using UIKit;
using CoreGraphics;
using Foundation;
using System.Globalization;
using WebKit;
using AmiiTag.Maui.Services;

namespace AmiiTag.Maui.Pages;

public sealed partial class BlazorPage
{
    private UIGestureRecognizer? _suspendedContentPopGesture;
    private bool _contentPopWasEnabled;

    private void UpdateFolderBackGesture(UINavigationController controller)
    {
        if (!_chrome.CanGoBack || !ReferenceEquals(MauiNavigator.CurrentPage, this))
        {
            RestoreContentPopGesture();
            return;
        }
        if (!(OperatingSystem.IsIOSVersionAtLeast(26) || OperatingSystem.IsMacCatalystVersionAtLeast(26))) return;

        // The content-wide recognizer bypasses MAUI's edge-back callback. Leave
        // UIKit's delegate untouched: suspend this recognizer while folders own
        // Back, allowing MAUI's existing edge gesture and toolbar to handle it.
        var gesture = controller.InteractiveContentPopGestureRecognizer;
        if (gesture == null) return;
        if (!ReferenceEquals(gesture, _suspendedContentPopGesture))
        {
            RestoreContentPopGesture();
            _contentPopWasEnabled = gesture.Enabled;
            _suspendedContentPopGesture = gesture;
        }
        if (gesture.Enabled) gesture.Enabled = false;
    }

    private void RestoreContentPopGesture()
    {
        var gesture = _suspendedContentPopGesture;
        _suspendedContentPopGesture = null;
        // UIKit owns this recognizer; never dispose it or read/replace its
        // private delegate. Restore only the enabled state saved on attachment.
        if (gesture != null && gesture.Handle != IntPtr.Zero)
            gesture.Enabled = _contentPopWasEnabled;
    }

    partial void SuspendNativeChrome() => RestoreContentPopGesture();

    private WKWebView? _nativeWebView;
    private UIRefreshControl? _nativeRefresh;
    private double _appliedTop = -1;
    private long _appliedContentGeneration = -1;
    private bool _initialScrollSet;
    private bool _applyingTopInset;
    private double _pendingTopInset;

    partial void InitializeNativeChrome(object webView)
    {
        if (webView is not WKWebView wk) return;
        _nativeWebView = wk;
        wk.Opaque = false;
        wk.BackgroundColor = UIColor.Clear;
        // MAUI extends this page under the translucent navigation bar. Apply the
        // overlap as HTML padding, never both native and WebKit automatic insets.
        wk.ScrollView.ContentInsetAdjustmentBehavior = UIScrollViewContentInsetAdjustmentBehavior.Never;
        wk.ScrollView.ContentInset = UIEdgeInsets.Zero;
        wk.ScrollView.ScrollIndicatorInsets = UIEdgeInsets.Zero;
        _nativeRefresh = new UIRefreshControl();
        _nativeRefresh.ValueChanged += RefreshRequested;
        UpdateNativeChrome();
    }

    private async void RefreshRequested(object? sender, EventArgs e) => await RefreshFromNative();

    partial void UpdateNativeChrome()
    {
        if (_nativeWebView == null) return;
        _nativeWebView.ScrollView.RefreshControl = _chrome.CanRefresh ? _nativeRefresh : null;
        _nativeWebView.ScrollView.AlwaysBounceVertical = _chrome.CanRefresh;
        if (!_chrome.IsRefreshing) _nativeRefresh?.EndRefreshing();

        // Restore the system material rather than MAUI's opaque default. No
        // custom background tint or blur style: UIKit supplies the OS appearance,
        // including Liquid Glass on iOS 26 and accessibility fallbacks.
        UIResponder? responder = _nativeWebView;
        while (responder != null && responder is not UIViewController) responder = responder.NextResponder;
        if ((responder as UIViewController)?.NavigationController is { } controller)
        {
            ExtendUnderBars(responder as UIViewController, controller);
            UpdateFolderBackGesture(controller);
            var bar = controller.NavigationBar;
            var appearance = new UINavigationBarAppearance();
            appearance.ConfigureWithDefaultBackground();
            bar.StandardAppearance = appearance;
            bar.ScrollEdgeAppearance = appearance;
            bar.CompactAppearance = appearance;
            bar.Translucent = true;
            // The bar is shared by every page on the stack, so mid-transition (popping back to home, say) it still
            // shows the outgoing page's state. A page without a bar goes by its own setting instead: only the status
            // bar, from the window, whose safe area never includes the navigation bar.
            double overlap;
            if (!NavigationPage.GetHasNavigationBar(this))
            {
                if (_nativeWebView.Window is not { } window) return;
                overlap = window.SafeAreaInsets.Top;
            }
            else if (!ReferenceEquals(MauiNavigator.CurrentPage, this))
            {
                // Covered by another page: keep what this page had rather than follow that page's bar.
                return;
            }
            else
            {
                overlap = bar.Hidden ? (double)_nativeWebView.SafeAreaInsets.Top
                    : Math.Max(0, _nativeWebView.ConvertRectFromView(bar.Bounds, bar).Bottom);
            }
            ApplyHtmlTopInset(overlap);
        }
    }

    /// <summary>
    /// Lays this page out under the navigation bar whether or not the bar is translucent at that moment. Otherwise UIKit
    /// starts the page below an opaque bar, and a page without one (home) only grows to the top of the screen once the
    /// bar has finished hiding, so its background appears late above the safe area when going back to it.
    /// </summary>
    private static void ExtendUnderBars(UIViewController? page, UINavigationController controller)
    {
        // The stack's child is MAUI's container around the page's own controller: set it on each level up to it.
        for (var vc = page; vc != null && !ReferenceEquals(vc, controller); vc = vc.ParentViewController)
        {
            if (!vc.ExtendedLayoutIncludesOpaqueBars) vc.ExtendedLayoutIncludesOpaqueBars = true;
            if (vc.EdgesForExtendedLayout != UIRectEdge.All) vc.EdgesForExtendedLayout = UIRectEdge.All;
        }
    }

    private async void ApplyHtmlTopInset(double top)
    {
        _pendingTopInset = top;
        if (_applyingTopInset) return;
        _applyingTopInset = true;
        try
        {
            while (_nativeWebView is { } wk)
            {
                var requestedTop = _pendingTopInset;
                var contentGeneration = _chrome.ContentGeneration;
                if (Math.Abs(_appliedTop - requestedTop) < 0.5 && contentGeneration == _appliedContentGeneration) return;
                var pixels = requestedTop.ToString("0.##", CultureInfo.InvariantCulture);
                var initialScroll = _initialScrollSet ? "" : "window.scrollTo(0, 0);";
                var result = await wk.EvaluateJavaScriptAsync("(() => { const root = document.documentElement; if (!root || !document.querySelector('#app .app-page')) return false; root.dataset.nativeIos = 'true'; root.style.setProperty('--native-top', '" + pixels + "px'); " + initialScroll + " return true; })()");
                if (!ReferenceEquals(_nativeWebView, wk)) return;
                if (result is not NSNumber number || !number.BoolValue)
                {
                    if (contentGeneration != _chrome.ContentGeneration) continue;
                    return;
                }
                _appliedTop = requestedTop;
                _appliedContentGeneration = contentGeneration;
                if (!_initialScrollSet) wk.ScrollView.SetContentOffset(CGPoint.Empty, false);
                _initialScrollSet = true;
                // Layout may have changed during JavaScript evaluation. Apply
                // only the newest pending inset and never reset scroll twice.
            }
        }
        catch (Exception ex) when (ex is NSErrorException or ObjectDisposedException)
        {
            // Initialization/disposal can race a layout pass. Retry on the next notification.
        }
        finally { _applyingTopInset = false; }
    }

    partial void ReleaseNativeChrome()
    {
        RestoreContentPopGesture();
        if (_nativeWebView != null) _nativeWebView.ScrollView.RefreshControl = null;
        if (_nativeRefresh != null)
        {
            _nativeRefresh.ValueChanged -= RefreshRequested;
            _nativeRefresh.Dispose();
        }
        _nativeRefresh = null;
        _nativeWebView = null;
    }
}

#endif
