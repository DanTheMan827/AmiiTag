using AmiiTag.Core.Interfaces;
using AmiiTag.Core.Pages;

namespace AmiiTag.Core.Services;

/// <summary>
/// Every screen in the app, in one place, so MAUI and the web navigate the same way.
/// </summary>
public static class AppRoutes
{
    private static IReadOnlyDictionary<string, object?> NoParameters { get; } = new Dictionary<string, object?>();

    public static AppRoute Home() => new(typeof(Pages.Home), "AmiiTag", "", NoParameters);

    public static AppRoute Library() =>
        new(typeof(LibraryPage), "Amiibo Library", "library", NoParameters, RoutePresentation.Modal);

    public static AppRoute Series(string seriesId, string title) =>
        new(typeof(SeriesPage), title, $"library/{Uri.EscapeDataString(seriesId)}",
            new Dictionary<string, object?> { [nameof(SeriesPage.SeriesId)] = seriesId });

    /// <summary>
    /// A tag held in <see cref="TagStore"/>.
    /// </summary>
    /// <param name="automatic">
    /// Opened by the main menu's background scan rather than a tap. Browsers make the history entry such a navigation
    /// leaves from skippable, so the web marks the page (?auto=1) for it to offer its own way back to the main menu.
    /// </param>
    public static AppRoute Tag(string tagKey, string title, bool modal, bool automatic = false) =>
        new(typeof(TagPage), title, $"tag/{tagKey}{(automatic ? "?auto=1" : string.Empty)}",
            new Dictionary<string, object?> { [nameof(TagPage.TagKey)] = tagKey },
            modal ? RoutePresentation.Modal : RoutePresentation.Push);

    /// <summary>
    /// A tag generated from a library figure.
    /// </summary>
    public static AppRoute Figure(string figureId, string title) =>
        new(typeof(TagPage), title, $"figure/{figureId}",
            new Dictionary<string, object?> { [nameof(TagPage.FigureId)] = figureId });

    public static AppRoute Qr(string tagKey, string title) =>
        new(typeof(QrPage), $"{title} QR Code", $"qr/{tagKey}",
            new Dictionary<string, object?> { [nameof(QrPage.TagKey)] = tagKey });

    public static AppRoute Games() =>
        new(typeof(GamesPage), "Game Info", "games", NoParameters, RoutePresentation.Modal);

    public static AppRoute Game(string gameKey, string title) =>
        new(typeof(GamePage), title, $"games/{Uri.EscapeDataString(gameKey)}",
            new Dictionary<string, object?> { [nameof(GamePage.GameKey)] = gameKey });

    public static AppRoute Devices() =>
        new(typeof(DevicesPage), "Devices", "devices", NoParameters, RoutePresentation.Modal);

    public static AppRoute Device(string deviceId, string title) =>
        new(typeof(DevicePage), title, $"device/{Uri.EscapeDataString(deviceId)}",
            new Dictionary<string, object?> { [nameof(DevicePage.DeviceId)] = deviceId });

    public static AppRoute N2Elite() =>
        new(typeof(N2ElitePage), "N2 Elite", "n2elite", NoParameters, RoutePresentation.Modal);

    /// <summary>
    /// The Flipper Zero emulation and its traffic log (<see cref="FlipperEmulationService"/>).
    /// </summary>
    public static AppRoute Emulate() =>
        new(typeof(EmulatePage), "Emulate", "emulate", NoParameters, RoutePresentation.Modal);

    /// <summary>
    /// The user's named lock-on responses (<see cref="LockOnLibrary"/>).
    /// </summary>
    public static AppRoute LockOns() =>
        new(typeof(LockOnsPage), "Lock-on Library", "lockons", NoParameters, RoutePresentation.Modal);

    /// <summary>
    /// The lock-on library as a picker for a v3 tag in <see cref="TagStore"/>: choosing one (or a file) puts it in the
    /// tag and comes back.
    /// </summary>
    public static AppRoute PickLockOn(string tagKey) =>
        new(typeof(LockOnsPage), "Load Lock-on", $"lockons/pick/{tagKey}",
            new Dictionary<string, object?> { [nameof(LockOnsPage.TagKey)] = tagKey });

    public static AppRoute Settings() =>
        new(typeof(SettingsPage), "Settings", "settings", NoParameters, RoutePresentation.Modal);
}
