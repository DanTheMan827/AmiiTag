namespace AmiiTag.Core.Interfaces;

public enum RoutePresentation
{
    /// <summary>Pushed onto the current navigation stack.</summary>
    Push,

    /// <summary>Presented as a sheet with its own navigation stack (iOS page sheet).</summary>
    Modal,
}

/// <summary>
/// A screen in the app. On MAUI it becomes a native page hosting <see cref="Component"/> in its own BlazorWebView;
/// on the web it's a route handled by the Blazor router.
/// </summary>
/// <param name="Component">The Blazor component that renders the screen.</param>
/// <param name="Title">The native/web title.</param>
/// <param name="WebPath">The relative URL for the web router (must match the component's @page template).</param>
/// <param name="Parameters">Component parameters (MAUI). The web gets the same values through <see cref="WebPath"/>.</param>
/// <param name="Presentation">How the screen is shown on MAUI.</param>
public sealed record AppRoute(
    Type Component,
    string Title,
    string WebPath,
    IReadOnlyDictionary<string, object?> Parameters,
    RoutePresentation Presentation = RoutePresentation.Push);

public interface IAppNavigator
{
    Task NavigateAsync(AppRoute route);

    /// <summary>
    /// Goes back one screen (closing a sheet if it's the sheet's first screen).
    /// </summary>
    Task GoBackAsync();

    /// <summary>
    /// Returns to the home screen, closing any sheets.
    /// </summary>
    Task GoHomeAsync();

    /// <summary>
    /// Closes every screen above the nearest one showing <paramref name="route"/>'s component (sheets included),
    /// or replaces the current screen with <paramref name="route"/> if it isn't open.
    /// </summary>
    Task PopToAsync(AppRoute route);
}
