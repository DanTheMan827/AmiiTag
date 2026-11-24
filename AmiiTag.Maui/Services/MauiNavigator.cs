using AmiiTag.Core.Interfaces;
using AmiiTag.Maui.Pages;
using iOSPage = Microsoft.Maui.Controls.PlatformConfiguration.iOSSpecific.Page;
using iOSPresentation = Microsoft.Maui.Controls.PlatformConfiguration.iOSSpecific.UIModalPresentationStyle;

namespace AmiiTag.Maui.Services;

/// <summary>
/// Turns <see cref="AppRoute"/>s into native pages: pushes onto the current NavigationPage, or presents a new
/// NavigationPage as a sheet for modal routes on Android and Mac.
/// </summary>
public sealed class MauiNavigator : IAppNavigator
{
    private readonly SyncedFigureImageFiles _figureImages;

    public MauiNavigator(SyncedFigureImageFiles figureImages)
    {
        _figureImages = figureImages;
    }

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

        // iOS keeps the system's own navigation bar (translucent, following light/dark, standard back button); the
        // other platforms get colours matching the pages.
        if (DeviceInfo.Platform != DevicePlatform.iOS && DeviceInfo.Platform != DevicePlatform.MacCatalyst && DeviceInfo.Platform != DevicePlatform.WinUI && DeviceInfo.Platform != DevicePlatform.Android)
        {
            navigationPage.SetAppThemeColor(NavigationPage.BarBackgroundColorProperty, Color.FromArgb("#E6F8F9FC"), Color.FromArgb("#E60D0F14"));
            navigationPage.SetAppThemeColor(NavigationPage.BarTextColorProperty, Colors.Black, Colors.White);
        }

        // Let UIKit choose Liquid Glass on iOS 26+, with the system material on older iOS.
        // Never add an opaque custom background on iOS/Mac Catalyst.
#if IOS
        if (!OperatingSystem.IsIOSVersionAtLeast(26))
            Microsoft.Maui.Controls.PlatformConfiguration.iOSSpecific.NavigationPage.SetIsNavigationBarTranslucent(navigationPage, true);
#endif
        if (DeviceInfo.Platform == DevicePlatform.WinUI || DeviceInfo.Platform == DevicePlatform.Android)
        {
            if (DeviceInfo.Platform == DevicePlatform.WinUI)
                navigationPage.SetAppThemeColor(VisualElement.BackgroundColorProperty, Color.FromArgb("#F8F9FC"), Color.FromArgb("#0D0F14"));
            else navigationPage.BackgroundColor = Colors.Transparent;
            navigationPage.BarBackgroundColor = Colors.Transparent;
            navigationPage.SetAppThemeColor(NavigationPage.BarTextColorProperty, Colors.Black, Colors.White);
        }
        else navigationPage.SetAppThemeColor(VisualElement.BackgroundColorProperty, Color.FromArgb("#F8F9FC"), Color.FromArgb("#0D0F14"));
        return navigationPage;
    }

    public Task NavigateAsync(AppRoute route) => MainThread.InvokeOnMainThreadAsync(async () =>
    {
        var page = new BlazorPage(route, _figureImages);
        var root = RootPage ?? throw new InvalidOperationException("The app has no window yet.");

        // iOS and Windows: every screen is pushed onto the one navigation stack, so iOS gets its standard navigation
        // (back button, swipe from the edge to go back) and Windows' back arrow always leads back to the main screen.
        // Android/Mac: modal routes are sheets.
        var asSheet = route.Presentation == RoutePresentation.Modal &&
                      DeviceInfo.Platform != DevicePlatform.WinUI && DeviceInfo.Platform != DevicePlatform.iOS && DeviceInfo.Platform != DevicePlatform.macOS && DeviceInfo.Platform != DevicePlatform.MacCatalyst;
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

    public Task OpenExternalAsync(string url) => MainThread.InvokeOnMainThreadAsync(() => Launcher.Default.OpenAsync(new Uri(url)));

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
    /// Sheets need an explicit Close button.
    /// </summary>
    private static void AddCloseButton(Page page, NavigationPage sheet)
    {
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
