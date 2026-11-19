using AmiiTag.Core.Interfaces;
using AmiiTag.Core.Services;

namespace AmiiTag.Maui.Services;

/// <summary>
/// Phones and tablets: the native action sheet stands in for a context menu (used for a right click with a mouse, or
/// a long press where the web view reports one).
/// </summary>
public sealed class SheetContextMenu : INativeContextMenu
{
    private readonly IDialogService _dialogs;

    public SheetContextMenu(IDialogService dialogs)
    {
        _dialogs = dialogs;
    }

    public async Task<int?> ShowAsync(string? title, IReadOnlyList<ContextMenuItem> items, double x, double y) =>
        await _dialogs.ChooseAsync(title ?? string.Empty, items.Select(i => i.Title).ToList());
}
