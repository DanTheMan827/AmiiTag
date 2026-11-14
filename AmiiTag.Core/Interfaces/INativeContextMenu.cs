using AmiiTag.Core.Services;

namespace AmiiTag.Core.Interfaces;

/// <summary>
/// The platform's own context menu, shown at a point of the current screen (CSS pixels from its top left).
/// Registered only where there is one (Windows: a WinUI flyout; phones and tablets: the action sheet).
/// </summary>
public interface INativeContextMenu
{
    /// <returns>The chosen index, -1 if dismissed, or null if it can't be shown here (the page draws its own instead).</returns>
    Task<int?> ShowAsync(string? title, IReadOnlyList<ContextMenuItem> items, double x, double y);
}
