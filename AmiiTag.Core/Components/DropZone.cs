using AmiiTag.Core.Interfaces;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.JSInterop;

namespace AmiiTag.Core.Components;

/// <summary>
/// An element that takes files dragged onto it from the desktop (or another app). The innermost enabled zone under
/// the pointer takes the drop and is highlighted while files are over it; drops anywhere else are refused, so the web
/// view never opens a dropped file itself.
/// </summary>
public sealed class DropZone : ComponentBase, IAsyncDisposable
{
    private ElementReference _element;
    private IJSObjectReference? _module;
    private IJSObjectReference? _registration;
    private DotNetObjectReference<DropZone>? _self;
    private bool _updating;
    private bool _disposed;

    [Inject] private IJSRuntime JS { get; set; } = default!;

    [Inject] private IDialogService Dialogs { get; set; } = default!;

    [Parameter] public RenderFragment? ChildContent { get; set; }

    /// <summary>
    /// The element rendered ("div", "li", "section"...), so a zone can be a list item or a page itself.
    /// </summary>
    [Parameter] public string Element { get; set; } = "div";

    [Parameter] public string? Class { get; set; }

    /// <summary>
    /// Shown at the top of the window while files are over the zone ("Drop to write to slot 3").
    /// </summary>
    [Parameter] public string? Label { get; set; }

    /// <summary>
    /// Off: the zone is a plain element and drops pass through to an outer zone (or are refused).
    /// </summary>
    [Parameter] public bool Enabled { get; set; } = true;

    [Parameter] public EventCallback<IReadOnlyList<PickedFile>> OnFiles { get; set; }

    [Parameter(CaptureUnmatchedValues = true)] public IReadOnlyDictionary<string, object>? Attributes { get; set; }

    private bool Active => Enabled && OnFiles.HasDelegate;

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, Element);
        builder.AddMultipleAttributes(1, Attributes);
        // "drop-zone" (also set by the script) styles it while files are dragged over the window.
        var css = Active ? $"{Class} drop-zone".Trim() : Class;
        builder.AddAttribute(2, "class", string.IsNullOrEmpty(css) ? null : css);
        if (Active && !string.IsNullOrEmpty(Label))
        {
            builder.AddAttribute(3, "data-drop-label", Label);
        }

        builder.AddElementReferenceCapture(4, element => _element = element);
        builder.AddContent(5, ChildContent);
        builder.CloseElement();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_updating || _disposed)
        {
            return;
        }

        _updating = true;
        try
        {
            _module ??= await JS.InvokeAsync<IJSObjectReference>("import", "./_content/AmiiTag.Core/js/drop-zone.js");
            if (firstRender)
            {
                await _module.InvokeVoidAsync("installGuard");
            }

            if (Active && _registration == null)
            {
                _self ??= DotNetObjectReference.Create(this);
                _registration = await _module.InvokeAsync<IJSObjectReference>("register", _element, _self);
            }
            else if (!Active && _registration != null)
            {
                await UnregisterAsync();
            }
        }
        catch (JSException)
        {
            // No drag and drop here; everything else still works.
        }
        catch (JSDisconnectedException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            _updating = false;
        }
    }

    [JSInvokable]
    public async Task OnFilesDropped(DroppedFile[] files)
    {
        var picked = new List<PickedFile>();
        var skipped = new List<string>();
        foreach (var file in files)
        {
            if (file.Base64 == null)
            {
                skipped.Add(file.Problem == "too large" ? $"{file.Name} (too large)" : file.Name);
                continue;
            }

            try
            {
                picked.Add(new PickedFile(file.Name, Convert.FromBase64String(file.Base64)));
            }
            catch (FormatException)
            {
                skipped.Add(file.Name);
            }
        }

        await InvokeAsync(async () =>
        {
            if (skipped.Count > 0)
            {
                await Dialogs.AlertAsync("Some items couldn't be read", $"Only files can be dropped here: {string.Join(", ", skipped)}.");
            }

            if (picked.Count == 0)
            {
                return;
            }

            try
            {
                await OnFiles.InvokeAsync(picked);
            }
            catch (Exception ex)
            {
                await Dialogs.AlertAsync("Couldn't use the dropped file", ex.Message);
            }
        });
    }

    private async Task UnregisterAsync()
    {
        var registration = _registration;
        _registration = null;
        if (registration != null)
        {
            try
            {
                await registration.InvokeVoidAsync("dispose");
                await registration.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
            }
            catch (JSException)
            {
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        await UnregisterAsync();
        if (_module != null)
        {
            try
            {
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
            }
        }

        _self?.Dispose();
    }

    public sealed record DroppedFile(string Name, string? Base64, string? Problem);
}
