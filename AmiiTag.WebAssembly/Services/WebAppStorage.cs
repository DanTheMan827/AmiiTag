using AmiiTag.Core.Interfaces;
using Microsoft.JSInterop;

namespace AmiiTag.WebAssembly.Services;

/// <summary>
/// Stores binary blobs in IndexedDB, including their last access time.
/// </summary>
public sealed class WebAppStorage : IAppStorage
{
    private readonly IJSRuntime _js;

    public WebAppStorage(IJSRuntime js)
    {
        _js = js;
    }

    public async Task<byte[]?> ReadAsync(string name) =>
        await _js.InvokeAsync<byte[]?>("amiitag.storage.read", name);

    public async Task WriteAsync(string name, byte[] data) =>
        await _js.InvokeVoidAsync("amiitag.storage.write", name, data);

    public async Task DeleteAsync(string name) => await _js.InvokeVoidAsync("amiitag.storage.remove", name);

    public async Task<IReadOnlyList<string>> ListAsync(string prefix) =>
        await _js.InvokeAsync<string[]>("amiitag.storage.list", prefix);
}

