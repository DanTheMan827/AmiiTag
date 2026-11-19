using AmiiTag.Maui.Services;
using Grid = Microsoft.UI.Xaml.Controls.Grid;
using Canvas = Microsoft.UI.Xaml.Controls.Canvas;
using WebView2 = Microsoft.UI.Xaml.Controls.WebView2;
using SizeChangedEventArgs = Microsoft.UI.Xaml.SizeChangedEventArgs;
using SolidColorBrush = Microsoft.UI.Xaml.Media.SolidColorBrush;
using VisualTreeHelper = Microsoft.UI.Xaml.Media.VisualTreeHelper;
using System.Globalization;
using System.Runtime.CompilerServices;
using WElement = Microsoft.UI.Xaml.DependencyObject;
using WView = Microsoft.UI.Xaml.FrameworkElement;

namespace AmiiTag.Maui.Pages;

public sealed partial class BlazorPage
{
    private WebView2? _nativeWebView;
    private sealed record ContentPlacement(Microsoft.UI.Xaml.Thickness Margin);
    private static readonly ConditionalWeakTable<WView, ContentPlacement> ContentPlacements = new();
    private double _appliedTop = -1;
    private long _appliedContentGeneration = -1;
    private double _pendingTopInset;
    private bool _applyingTopInset;
    private bool _captionOnlyUnderlay;

    partial void InitializeNativeChrome(object webView)
    {
        if (webView is not WebView2 web) return;
        _nativeWebView = web;
        web.Loaded += OnNativeWebViewLoaded;
        web.SizeChanged += OnNativeWebViewSizeChanged;
        web.LayoutUpdated += OnNativeLayoutUpdated;
        UpdateNativeChrome();
    }

    private void OnNativeWebViewLoaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => UpdateNativeChrome();

    private void OnNativeLayoutUpdated(object? sender, object e) => UpdateNativeChrome();

    private void OnNativeWebViewSizeChanged(object sender, SizeChangedEventArgs e) => UpdateNativeChrome();

    private static T? FindDescendant<T>(WElement? parent) where T : WElement
    {
        if (parent is T found) return found;
        if (parent == null) return null;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindDescendant<T>(VisualTreeHelper.GetChild(parent, i)) is { } child) return child;
        return null;
    }

    private static bool ExtendUnderToolbar(WebView2 web, Microsoft.Maui.Platform.MauiToolbar toolbar, double captionHeight)
    {
        // Let the native content frame occupy the toolbar row as well as the page
        // row. The native toolbar stays above that frame in the visual stack.
        WElement webBranch = web;
        while (VisualTreeHelper.GetParent(webBranch) is { } parent)
        {
            WElement barBranch = toolbar;
            while (VisualTreeHelper.GetParent(barBranch) is { } barParent)
            {
                if (!ReferenceEquals(webBranch, barBranch) && ReferenceEquals(parent, barParent) && parent is Grid && webBranch is WView content && barBranch is WView bar)
                {
                    var original = ContentPlacements.GetValue(content, view => new ContentPlacement(view.Margin));
                    var margin = original.Margin;
                    var extendedMargin = new Microsoft.UI.Xaml.Thickness(margin.Left, margin.Top - captionHeight, margin.Right, margin.Bottom);
                    if (!content.Margin.Equals(extendedMargin)) content.Margin = extendedMargin;
                    var contentRow = Grid.GetRow(content);
                    var barRow = Grid.GetRow(bar);
                    if (contentRow > barRow)
                    {
                        Grid.SetRowSpan(content, Grid.GetRowSpan(content) + contentRow - barRow);
                        Grid.SetRow(content, barRow);
                    }
                    if (Canvas.GetZIndex(bar) != 100) Canvas.SetZIndex(bar, 100);
                    if (Canvas.GetZIndex(content) != 0) Canvas.SetZIndex(content, 0);
                    return true;
                }
                barBranch = barParent;
            }
            webBranch = parent;
        }
        return false;
    }

    partial void UpdateNativeChrome()
    {
        var web = _nativeWebView;
        if (web == null || !ReferenceEquals(MauiNavigator.CurrentPage, this)) return;
        var nativeWindow = Window?.Handler?.PlatformView as Microsoft.UI.Xaml.Window;
        if (nativeWindow == null) return;
        // OnWindowCreated precedes MAUI's title-bar/theme mappings. Reassert
        // the transparent caption after the host is attached, including Home.
        WindowsBackdrop.Apply(nativeWindow);
        WindowsBackdrop.RemoveUnderlayClipping(web);
        var scale = nativeWindow.Content?.XamlRoot?.RasterizationScale ?? 1;
        var captionHeight = nativeWindow.ExtendsContentIntoTitleBar ? nativeWindow.AppWindow.TitleBar.Height / scale : 0;
        var toolbar = FindDescendant<Microsoft.Maui.Platform.MauiToolbar>(nativeWindow.Content);
        double top;
        try
        {
            var webTop = web.TransformToVisual(nativeWindow.Content)
                .TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
            top = Math.Max(0, captionHeight - webTop);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.Runtime.InteropServices.COMException) { return; }
        if (toolbar == null)
        {
            // Home has no app toolbar, especially before its template exists.
            // Extend the WebView itself instead of waiting for a toolbar row.
            var original = ContentPlacements.GetValue(web, view => new ContentPlacement(view.Margin));
            var margin = original.Margin;
            var underlayMargin = new Microsoft.UI.Xaml.Thickness(margin.Left, margin.Top - captionHeight, margin.Right, margin.Bottom);
            if (!web.Margin.Equals(underlayMargin)) web.Margin = underlayMargin;
            _captionOnlyUnderlay = true;
        }
        else
        {
            if (_captionOnlyUnderlay && ContentPlacements.TryGetValue(web, out var original))
            {
                web.Margin = original.Margin;
                _captionOnlyUnderlay = false;
            }
            if (toolbar.Background is not SolidColorBrush brush || brush.Color.A != 0)
                toolbar.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            ExtendUnderToolbar(web, toolbar, captionHeight);
            if (NavigationPage.GetHasNavigationBar(this))
            {
                try
                {
                    top = Math.Max(top, toolbar.TransformToVisual(web)
                        .TransformPoint(new Windows.Foundation.Point(0, toolbar.ActualHeight)).Y);
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.Runtime.InteropServices.COMException) { return; }
            }
        }
        _ = ApplyHtmlTopInset(web, top);
    }

    private async Task ApplyHtmlTopInset(WebView2 web, double top)
    {
        _pendingTopInset = top;
        if (_applyingTopInset) return;
        _applyingTopInset = true;
        try
        {
            while (ReferenceEquals(_nativeWebView, web))
            {
                var requestedTop = _pendingTopInset;
                var contentGeneration = _chrome.ContentGeneration;
                if (Math.Abs(_appliedTop - requestedTop) < 0.5 && contentGeneration == _appliedContentGeneration) return;
                if (web.CoreWebView2 == null) return;
                var pixels = requestedTop.ToString("0.##", CultureInfo.InvariantCulture);
                var barHeight = NavigationPage.GetHasNavigationBar(this) ? pixels + "px" : "0px";
                var result = await web.CoreWebView2.ExecuteScriptAsync("(() => { const r=document.documentElement; if(!r || !document.querySelector('#app .app-page')) return false; r.dataset.nativeDesktop='true'; r.style.setProperty('--native-top','" + pixels + "px'); r.style.setProperty('--native-bar-height','" + barHeight + "'); return true; })();");
                if (!ReferenceEquals(_nativeWebView, web)) return;
                if (result != "true")
                {
                    if (contentGeneration != _chrome.ContentGeneration) continue;
                    return;
                }
                _appliedTop = requestedTop;
                _appliedContentGeneration = contentGeneration;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // WebView disposal can race a native layout notification.
        }
        finally { _applyingTopInset = false; }
    }

    partial void ReleaseNativeChrome()
    {
        if (_nativeWebView != null)
        {
            _nativeWebView.Loaded -= OnNativeWebViewLoaded;
            _nativeWebView.SizeChanged -= OnNativeWebViewSizeChanged;
            _nativeWebView.LayoutUpdated -= OnNativeLayoutUpdated;
        }
        _nativeWebView = null;
    }
}
