using DependencyObject = Microsoft.UI.Xaml.DependencyObject;
using UIElement = Microsoft.UI.Xaml.UIElement;
using VisualTreeHelper = Microsoft.UI.Xaml.Media.VisualTreeHelper;

namespace AmiiTag.Maui.Services;

internal static class WindowsBackdrop
{
    public static void Apply(Microsoft.UI.Xaml.Window window)
    {
        // The opaque shared web page paints the window, including the caption.
        // Only the native chrome is transparent; there is no desktop backdrop.
        if (window.SystemBackdrop != null) window.SystemBackdrop = null;
        if (!window.ExtendsContentIntoTitleBar) window.ExtendsContentIntoTitleBar = true;
        var titleBar = window.AppWindow.TitleBar;
        if (titleBar.BackgroundColor != Microsoft.UI.Colors.Transparent) titleBar.BackgroundColor = Microsoft.UI.Colors.Transparent;
        if (titleBar.InactiveBackgroundColor != Microsoft.UI.Colors.Transparent) titleBar.InactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        if (titleBar.ButtonBackgroundColor != Microsoft.UI.Colors.Transparent) titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        if (titleBar.ButtonInactiveBackgroundColor != Microsoft.UI.Colors.Transparent) titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
    }

    public static void RemoveUnderlayClipping(DependencyObject element)
    {
        // Allow the opaque WebView to extend beneath the toolbar and caption
        // without changing WebView2 or ancestor background colours.
        DependencyObject? current = element;
        while (current != null)
        {
            if (current is UIElement view && view.Clip != null) view.Clip = null;
            current = VisualTreeHelper.GetParent(current);
        }
    }
}
