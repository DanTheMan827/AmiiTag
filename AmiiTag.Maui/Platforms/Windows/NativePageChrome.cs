using AmiiTag.Maui.Services;
using Grid = Microsoft.UI.Xaml.Controls.Grid;
using Canvas = Microsoft.UI.Xaml.Controls.Canvas;
using WebView2 = Microsoft.UI.Xaml.Controls.WebView2;
using SizeChangedEventArgs = Microsoft.UI.Xaml.SizeChangedEventArgs;
using SolidColorBrush = Microsoft.UI.Xaml.Media.SolidColorBrush;
using VisualTreeHelper = Microsoft.UI.Xaml.Media.VisualTreeHelper;
using System.Globalization;
using WElement = Microsoft.UI.Xaml.DependencyObject;
using WView = Microsoft.UI.Xaml.FrameworkElement;

namespace AmiiTag.Maui.Pages;

public sealed partial class BlazorPage
{
    private WebView2? _nativeWebView;
    // Where MAUI first put an element, so the extension under the caption and toolbar is always applied to that and
    // never on top of an earlier extension. It's kept on the element itself, in an attached property: the managed
    // wrapper VisualTreeHelper returns for a XAML element isn't always the same object, so a table keyed by the wrapper
    // forgot the original and stacked another -32 on the margin each time (the WebView grew above the window).
    private sealed record ContentPlacement(Microsoft.UI.Xaml.Thickness Margin, int Row = 0, int RowSpan = 1);

    private static readonly Microsoft.UI.Xaml.DependencyProperty OriginalPlacementProperty =
        Microsoft.UI.Xaml.DependencyProperty.RegisterAttached("AmiiTagOriginalPlacement", typeof(string), typeof(BlazorPage),
            new Microsoft.UI.Xaml.PropertyMetadata(null));

    private static ContentPlacement GetOriginalPlacement(WView view)
    {
        if (TryGetOriginalPlacement(view, out var stored))
        {
            return stored;
        }

        var placement = new ContentPlacement(view.Margin, Grid.GetRow(view), Grid.GetRowSpan(view));
        var m = placement.Margin;
        view.SetValue(OriginalPlacementProperty, string.Join(',', new[] { m.Left, m.Top, m.Right, m.Bottom, placement.Row, placement.RowSpan }
            .Select(value => value.ToString(CultureInfo.InvariantCulture))));
        return placement;
    }

    private static bool TryGetOriginalPlacement(WView view, out ContentPlacement placement)
    {
        placement = null!;
        if (view.GetValue(OriginalPlacementProperty) is not string text)
        {
            return false;
        }

        var parts = text.Split(',').Select(part => double.Parse(part, CultureInfo.InvariantCulture)).ToArray();
        placement = new ContentPlacement(new Microsoft.UI.Xaml.Thickness(parts[0], parts[1], parts[2], parts[3]), (int)parts[4], (int)parts[5]);
        return true;
    }
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
                    var original = GetOriginalPlacement(content);
                    var margin = original.Margin;
                    var extendedMargin = new Microsoft.UI.Xaml.Thickness(margin.Left, margin.Top - captionHeight, margin.Right, margin.Bottom);
                    var changed = false;
                    if (!content.Margin.Equals(extendedMargin)) { content.Margin = extendedMargin; changed = true; }

                    // Absolute values from the original placement: from the toolbar's row down to where it ended.
                    var barRow = Grid.GetRow(bar);
                    var row = Math.Min(original.Row, barRow);
                    var span = original.RowSpan + original.Row - row;
                    if (Grid.GetRow(content) != row) { Grid.SetRow(content, row); changed = true; }
                    if (Grid.GetRowSpan(content) != span) { Grid.SetRowSpan(content, span); changed = true; }
#if DEBUG
                    if (changed)
                    {
                        System.Diagnostics.Debug.WriteLine($"Chrome: content row {row} span {span} margin {extendedMargin.Top:0.#}, web {web.ActualHeight:0.#} high");
                    }
#endif
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
            var original = GetOriginalPlacement(web);
            var margin = original.Margin;
            var underlayMargin = new Microsoft.UI.Xaml.Thickness(margin.Left, margin.Top - captionHeight, margin.Right, margin.Bottom);
            if (!web.Margin.Equals(underlayMargin)) web.Margin = underlayMargin;
            _captionOnlyUnderlay = true;
        }
        else
        {
            if (_captionOnlyUnderlay && TryGetOriginalPlacement(web, out var original))
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
#if DEBUG
        TraceWebViewPlacement(web, captionHeight, top, toolbar != null);
#endif
        _ = ApplyHtmlTopInset(web, top);
    }

#if DEBUG
    private double _tracedHeight = -1;

    // Debug builds: whenever the WebView's height changes, every ancestor's placement (type, row/span, top margin,
    // height), to find what makes it grow.
    private void TraceWebViewPlacement(WebView2 web, double captionHeight, double top, bool hasToolbar)
    {
        if (Math.Abs(web.ActualHeight - _tracedHeight) < 0.5) return;
        _tracedHeight = web.ActualHeight;
        var lines = new System.Text.StringBuilder();
        lines.Append($"Chrome: web {web.ActualHeight:0.#} high, top inset {top:0.#}, caption {captionHeight:0.#}, toolbar {hasToolbar}");
        WElement? current = web;
        for (var depth = 0; current != null && depth < 16; depth++, current = VisualTreeHelper.GetParent(current))
        {
            if (current is WView view)
            {
                lines.Append($"\n  {view.GetType().Name}: row {Grid.GetRow(view)} span {Grid.GetRowSpan(view)}, margin top {view.Margin.Top:0.#}, height {view.ActualHeight:0.#}");
            }
        }

        System.Diagnostics.Debug.WriteLine(lines.ToString());
    }
#endif

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
