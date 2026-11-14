using AmiiTag.Core.Interfaces;
using AmiiTag.Maui.Pages;
using iOSPage = Microsoft.Maui.Controls.PlatformConfiguration.iOSSpecific.Page;
using iOSPresentation = Microsoft.Maui.Controls.PlatformConfiguration.iOSSpecific.UIModalPresentationStyle;

namespace AmiiTag.Maui.Services;

/// <summary>
/// Turns <see cref="AppRoute"/>s into native pages: pushes onto the current NavigationPage, or presents a new
/// NavigationPage as a sheet for modal routes (like the Swift app's page sheets).
/// </summary>
public sealed class MauiNavigator : IAppNavigator
{
    internal static Page? RootPage => Application.Current?.Windows.FirstOrDefault()?.Page;

    /// <summary>
    /// The navigation stack screens should be pushed onto: the top sheet's, or the root's.
    /// </summary>
    internal static INavigation? CurrentNavigation
    {
        get
        {
            var root = RootPage;
            if (root == null)
            {
                return null;
            }

            var sheet = root.Navigation.ModalStack.LastOrDefault(page => page is NavigationPage);
            return sheet?.Navigation ?? root.Navigation;
        }
    }

    /// <summary>
    /// The page currently on screen (ignoring progress overlays), used to show alerts.
    /// </summary>
    internal static Page? CurrentPage
    {
        get
        {
            var root = RootPage;
            if (root == null)
            {
                return null;
            }

            var top = root.Navigation.ModalStack.LastOrDefault() ?? root;
            return top is NavigationPage navigationPage ? navigationPage.CurrentPage : top;
        }
    }

    /// <summary>
    /// Whether a page is still on any navigation stack (the root's or a sheet's): false once it has been popped or its
    /// sheet dismissed.
    /// </summary>
    internal static bool IsOnStack(Page page)
    {
        var root = RootPage;
        if (root == null)
        {
            return false;
        }

        if (root.Navigation.NavigationStack.Contains(page))
        {
            return true;
        }

        return root.Navigation.ModalStack.Any(modal =>
            modal == page || (modal is NavigationPage navigation && navigation.Navigation.NavigationStack.Contains(page)));
    }

    public static NavigationPage CreateNavigationPage(Page page)
    {
        var navigationPage = new NavigationPage(page);
        navigationPage.SetAppThemeColor(NavigationPage.BarBackgroundColorProperty, Color.FromArgb("#F2F2F7"), Color.FromArgb("#1C1C1E"));
        navigationPage.SetAppThemeColor(NavigationPage.BarTextColorProperty, Colors.Black, Colors.White);
        return navigationPage;
    }

    public Task NavigateAsync(AppRoute route) => MainThread.InvokeOnMainThreadAsync(async () =>
    {
        var page = new BlazorPage(route);
        var root = RootPage ?? throw new InvalidOperationException("The app has no window yet.");

        // iOS: every screen is a native card (page sheet) that slides up and stacks on the previous one; swiping down
        // closes it. Android/Mac: only modal routes are sheets. Windows: everything is pushed, so the navigation bar's
        // back arrow always leads back to the main screen.
        var asSheet = DeviceInfo.Platform == DevicePlatform.iOS ||
                      (route.Presentation == RoutePresentation.Modal && DeviceInfo.Platform != DevicePlatform.WinUI);
        if (asSheet)
        {
            var sheet = CreateNavigationPage(page);
            sheet.SetValue(iOSPage.ModalPresentationStyleProperty, iOSPresentation.PageSheet);
            AddCloseButton(page, sheet);
            await root.Navigation.PushModalAsync(sheet);
        }
        else
        {
            await (CurrentNavigation ?? root.Navigation).PushAsync(page);
        }
    });

    public Task GoBackAsync() => MainThread.InvokeOnMainThreadAsync(async () =>
    {
        var root = RootPage;
        if (root == null)
        {
            return;
        }

        var sheet = root.Navigation.ModalStack.LastOrDefault(page => page is NavigationPage);
        if (sheet != null)
        {
            if (sheet.Navigation.NavigationStack.Count > 1)
            {
                await sheet.Navigation.PopAsync();
            }
            else
            {
                await root.Navigation.PopModalAsync();
            }

            return;
        }

        if (root.Navigation.NavigationStack.Count > 1)
        {
            await root.Navigation.PopAsync();
        }
    });

    public Task GoHomeAsync() => MainThread.InvokeOnMainThreadAsync(async () =>
    {
        var root = RootPage;
        if (root == null)
        {
            return;
        }

        while (root.Navigation.ModalStack.Count > 0)
        {
            await root.Navigation.PopModalAsync(false);
        }

        await root.Navigation.PopToRootAsync();
    });

    public Task PopToAsync(AppRoute route) => MainThread.InvokeOnMainThreadAsync(async () =>
    {
        var root = RootPage;
        if (root == null)
        {
            return;
        }

        // Screens in order: the root stack, then each sheet's stack.
        var sheets = root.Navigation.ModalStack.OfType<NavigationPage>().ToList();
        var target = sheets.AsEnumerable().Reverse()
            .SelectMany(sheet => sheet.Navigation.NavigationStack.Reverse())
            .Concat(root.Navigation.NavigationStack.Reverse())
            .OfType<BlazorPage>()
            .FirstOrDefault(page => page.Route.Component == route.Component);

        if (target == null)
        {
            await NavigateAsync(route);
            return;
        }

        // Close sheets above the one holding the target (a progress overlay on top is left for its owner to close).
        while (root.Navigation.ModalStack.LastOrDefault() is NavigationPage top && !top.Navigation.NavigationStack.Contains(target))
        {
            await root.Navigation.PopModalAsync(animated: false);
        }

        var navigation = root.Navigation.ModalStack.LastOrDefault() is NavigationPage holder && holder.Navigation.NavigationStack.Contains(target)
            ? holder.Navigation
            : root.Navigation;

        while (navigation.NavigationStack.Count > 1 && navigation.NavigationStack[^1] != target)
        {
            await navigation.PopAsync(animated: navigation.NavigationStack[^2] == target);
        }
    });

    /// <summary>
    /// Sheets can be swiped away on iOS; elsewhere they need an explicit Close button.
    /// </summary>
    private static void AddCloseButton(Page page, NavigationPage sheet)
    {
        if (DeviceInfo.Platform == DevicePlatform.iOS)
        {
            return;
        }

        page.ToolbarItems.Add(new ToolbarItem
        {
            Text = "Close",
            Order = ToolbarItemOrder.Primary,
            Command = new Command(async () =>
            {
                if (RootPage is { } root && root.Navigation.ModalStack.Contains(sheet))
                {
                    await root.Navigation.PopModalAsync();
                }
            }),
        });
    }
}
