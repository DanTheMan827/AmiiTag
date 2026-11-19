using AmiiTag.AmiiboData;
using AmiiTag.Core.Interfaces;
using Microsoft.JSInterop;

namespace AmiiTag.WebAssembly.Services;

/// <summary>Displays persisted image Blobs through document-scoped object URLs.</summary>
public sealed class WebFigureImageProvider(IJSRuntime js) : IFigureImageProvider, IAsyncDisposable
{
    public async ValueTask<string?> GetImageUrlAsync(AmiiboFigure figure) => figure.ImageUri == null ? null :
        await js.InvokeAsync<string?>("amiitag.assets.imageUrl", figure.ImageUri.AbsoluteUri);

    public async ValueTask DisposeAsync()
    {
        try { await js.InvokeVoidAsync("amiitag.assets.releaseImageUrls"); }
        catch (JSDisconnectedException) { }
    }
}
