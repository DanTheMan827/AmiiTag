using AmiiTag.Core.Data;
using AmiiTag.Core.Interfaces;
using Microsoft.AspNetCore.Components.Web;

namespace AmiiTag.Core.Services;

/// <summary>
/// One entry of a context menu.
/// </summary>
/// <param name="Title">The menu text.</param>
/// <param name="Run">What it does.</param>
/// <param name="IsDestructive">Shown in red (delete, wipe...).</param>
/// <param name="IsDefault">What a plain click does on desktop (normally "Open").</param>
public sealed record ContextMenuItem(string Title, Func<Task> Run, bool IsDestructive = false, bool IsDefault = false);

/// <summary>
/// Context menus for list rows. On Windows, Mac and the web a click runs the row's default action (normally Open) and
/// a right click opens the menu; a tap with a finger there opens the menu as a sheet, as there's no right click. The
/// Android and iOS apps always show the menu, as an action sheet, on tap (or a long press where it's reported).
/// The menu is the platform's own where it can be shown at the pointer (<see cref="INativeContextMenu"/>), otherwise
/// a menu drawn in the page.
/// </summary>
/// <remarks>
/// Scoped: one per web view, so the in-page menu appears on the screen that asked for it. Rows wire up like:
/// <code>
/// &lt;button @onpointerdown="Menus.TrackPointer" @onclick="e => Menus.ClickAsync(e, title, items)"
///         @oncontextmenu="e => Menus.ShowAsync(e, title, items)" @oncontextmenu:preventDefault="true"&gt;
/// </code>
/// </remarks>
public sealed class ContextMenus
{
    private readonly IDialogService _dialogs;
    private readonly INativeContextMenu? _native;
    private readonly bool _desktop;
    private string? _pointerType;

    public ContextMenus(IDialogService dialogs, IServiceProvider services)
    {
        _dialogs = dialogs;
        _native = services.GetService(typeof(INativeContextMenu)) as INativeContextMenu;
        var platform = (services.GetService(typeof(AppEnvironment)) as AppEnvironment)?.Platform ?? AppEnvironment.PlatformType.Unknown;
        _desktop = platform is not (AppEnvironment.PlatformType.Android or AppEnvironment.PlatformType.iOS);
    }

    /// <summary>
    /// True where a click runs the default action and the menu is on right click (Windows, Mac, web).
    /// </summary>
    public bool UsesDefaultAction => _desktop;

    /// <summary>
    /// Set by the in-page menu (<c>ContextMenuOverlay</c>) while it's on screen: shows a menu at a point and returns the
    /// chosen index, or -1.
    /// </summary>
    internal Func<double, double, string?, IReadOnlyList<ContextMenuItem>, Task<int>>? Presenter { get; set; }

    /// <summary>
    /// Remembers whether the coming click is from a mouse or a finger.
    /// </summary>
    public void TrackPointer(PointerEventArgs e) => _pointerType = e.PointerType;

    /// <summary>
    /// A click on a row. Windows, Mac and web: the default action with a mouse or keyboard (or the menu at the pointer
    /// when the row has none), the menu as a sheet with touch. Android and iOS: the menu as a sheet.
    /// </summary>
    /// <param name="tapShowsMenu">
    /// False for rows whose tap has always just opened (the library, the device list): they open on tap everywhere, and
    /// their menu is only on right click or long press.
    /// </param>
    public async Task ClickAsync(MouseEventArgs e, string? title, IReadOnlyList<ContextMenuItem> items, bool tapShowsMenu = true)
    {
        var touch = tapShowsMenu && (!_desktop || _pointerType is "touch" or "pen");
        _pointerType = null;
        if (items.Count == 0)
        {
            return;
        }

        if (touch)
        {
            await RunChoiceAsync(items, await ChooseAsSheetAsync(title, items));
            return;
        }

        if (items.FirstOrDefault(i => i.IsDefault) is { } defaultItem)
        {
            await defaultItem.Run();
            return;
        }

        await ShowAsync(e, title, items);
    }

    /// <summary>
    /// A right click (or long press): the context menu at the pointer.
    /// </summary>
    public async Task ShowAsync(MouseEventArgs e, string? title, IReadOnlyList<ContextMenuItem> items)
    {
        _pointerType = null;
        if (items.Count == 0)
        {
            return;
        }

        int? index = null;
        if (_native != null)
        {
            index = await _native.ShowAsync(title, items, e.ClientX, e.ClientY);
        }

        if (index == null && Presenter is { } presenter)
        {
            index = await presenter(e.ClientX, e.ClientY, title, items);
        }

        index ??= await ChooseAsSheetAsync(title, items);
        await RunChoiceAsync(items, index.Value);
    }

    private Task<int> ChooseAsSheetAsync(string? title, IReadOnlyList<ContextMenuItem> items) =>
        _dialogs.ChooseAsync(title ?? string.Empty, items.Select(i => i.Title).ToList());

    private static Task RunChoiceAsync(IReadOnlyList<ContextMenuItem> items, int index) =>
        index >= 0 && index < items.Count ? items[index].Run() : Task.CompletedTask;
}
