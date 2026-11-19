using AmiiTag.Core.Interfaces;
using AmiiTag.Core.Services;
using AmiiTag.Maui.Pages;
using AmiiTag.Maui.Services;
using Microsoft.AspNetCore.Components.WebView.Maui;
using Microsoft.UI.Xaml.Controls.Primitives;
using FrameworkElement = Microsoft.UI.Xaml.FrameworkElement;
using MenuFlyout = Microsoft.UI.Xaml.Controls.MenuFlyout;
using MenuFlyoutItem = Microsoft.UI.Xaml.Controls.MenuFlyoutItem;
using MenuFlyoutSeparator = Microsoft.UI.Xaml.Controls.MenuFlyoutSeparator;

namespace AmiiTag.Maui.WinUI;

/// <summary>
/// Windows: a native WinUI menu flyout, opened at the pointer over the current screen's WebView2.
/// </summary>
public sealed class WindowsContextMenu : INativeContextMenu
{
    public Task<int?> ShowAsync(string? title, IReadOnlyList<ContextMenuItem> items, double x, double y) =>
        MainThread.InvokeOnMainThreadAsync(() => Show(items, x, y));

    private static Task<int?> Show(IReadOnlyList<ContextMenuItem> items, double x, double y)
    {
        if (MauiNavigator.CurrentPage is not BlazorPage { Content: BlazorWebView { Handler.PlatformView: FrameworkElement view } })
        {
            return Task.FromResult<int?>(null);
        }

        var choice = new TaskCompletionSource<int?>();
        var flyout = new MenuFlyout();
        for (var i = 0; i < items.Count; i++)
        {
            var index = i;
            var entry = items[i];

            // Destructive commands sit apart, as Windows menus do.
            if (entry.IsDestructive && i > 0 && !items[i - 1].IsDestructive)
            {
                flyout.Items.Add(new MenuFlyoutSeparator());
            }

            var item = new MenuFlyoutItem { Text = entry.Title };
            if (entry.IsDefault)
            {
                // Windows shows a context menu's default command in bold.
                item.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            }

            item.Click += (_, _) => choice.TrySetResult(index);
            flyout.Items.Add(item);
        }

        // Closed can come before Click: let a click win.
        flyout.Closed += (_, _) => view.DispatcherQueue.TryEnqueue(() => choice.TrySetResult(-1));

        // CSS pixels are WebView2 DIPs at the default zoom.
        flyout.ShowAt(view, new FlyoutShowOptions
        {
            Position = new global::Windows.Foundation.Point(x, y),
            ShowMode = FlyoutShowMode.Standard,
        });

        return choice.Task;
    }
}
