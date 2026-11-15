using AmiiTag.Core.Interfaces;
using AmiiTag.Nfc;
using AmiiTag.Nfc.Mfrc522;
using Microsoft.JSInterop;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;

namespace AmiiTag.WebAssembly.Services;

/// <summary>
/// Stores blobs in localStorage (base64).
/// </summary>
public sealed class WebAppStorage : IAppStorage
{
    private readonly IJSRuntime _js;

    public WebAppStorage(IJSRuntime js)
    {
        _js = js;
    }

    public async Task<byte[]?> ReadAsync(string name)
    {
        var value = await _js.InvokeAsync<string?>("amiitag.storage.read", name);
        return value == null ? null : Convert.FromBase64String(value);
    }

    public async Task WriteAsync(string name, byte[] data) =>
        await _js.InvokeVoidAsync("amiitag.storage.write", name, Convert.ToBase64String(data));

    public async Task DeleteAsync(string name) => await _js.InvokeVoidAsync("amiitag.storage.remove", name);

    public async Task<IReadOnlyList<string>> ListAsync(string prefix) =>
        await _js.InvokeAsync<string[]>("amiitag.storage.list", prefix);
}

public sealed class WebFileService : IFileService
{
    private readonly IJSRuntime _js;

    public WebFileService(IJSRuntime js)
    {
        _js = js;
    }

    public async Task<PickedFile?> PickFileAsync(string title, IReadOnlyList<string> extensions)
    {
        var accept = extensions.Count == 0 ? null : string.Join(",", extensions);
        var file = await _js.InvokeAsync<JsFile?>("amiitag.files.pick", accept);
        return file == null ? null : new PickedFile(file.Name, Convert.FromBase64String(file.Base64));
    }

    public async Task<IReadOnlyList<PickedFile>> PickFilesAsync(string title, IReadOnlyList<string> extensions)
    {
        var accept = extensions.Count == 0 ? null : string.Join(",", extensions);
        var files = await _js.InvokeAsync<JsFile[]?>("amiitag.files.pickMany", accept);
        return (files ?? Array.Empty<JsFile>()).Select(f => new PickedFile(f.Name, Convert.FromBase64String(f.Base64))).ToList();
    }

    public async Task SaveFileAsync(string fileName, byte[] data, string contentType = "application/octet-stream") =>
        await _js.InvokeVoidAsync("amiitag.files.download", fileName, data, contentType);

    private sealed record JsFile(string Name, string Base64);
}

/// <summary>
/// Camera QR scanning: BarcodeDetector where available, otherwise frames are decoded here with ZXing.Net.
/// </summary>
public sealed class WebQrScanner : IQrScanner
{
    private readonly IJSRuntime _js;
    private readonly QRCodeReader _reader = new();

    public WebQrScanner(IJSRuntime js)
    {
        _js = js;
    }

    public bool IsSupported => true;

    public async Task<bool> HasCameraAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _js.InvokeAsync<bool>("amiitag.qr.hasCamera", cancellationToken);
        }
        catch (JSException)
        {
            return false;
        }
    }

    public async Task<string?> ScanAsync(CancellationToken cancellationToken = default)
    {
        using var reference = DotNetObjectReference.Create(this);
        try
        {
            return await _js.InvokeAsync<string?>("amiitag.qr.scan", cancellationToken, reference);
        }
        catch (JSException ex)
        {
            throw new InvalidOperationException($"The camera couldn't be opened: {ex.Message}", ex);
        }
    }

    [JSInvokable]
    public string? Decode(int width, int height, byte[] rgba)
    {
        try
        {
            var source = new RGBLuminanceSource(rgba, width, height, RGBLuminanceSource.BitmapFormat.RGBA32);
            var result = _reader.decode(new BinaryBitmap(new HybridBinarizer(source)), new Dictionary<DecodeHintType, object>
            {
                [DecodeHintType.TRY_HARDER] = true,
            });
            return result?.Text;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Web Serial ports for an MFRC522 on a USB serial adapter (Chrome/Edge on desktop).
/// </summary>
public sealed class WebSerialPortFactory : ISerialPortFactory, ISerialPortRequester
{
    private readonly IJSRuntime _js;
    private bool? _supported;

    public WebSerialPortFactory(IJSRuntime js)
    {
        _js = js;
    }

    public bool IsSupported => _supported ??= ((IJSInProcessRuntime)_js).Invoke<bool>("amiitag.serial.isSupported");

    public async Task<IReadOnlyList<NfcReaderInfo>> GetPortsAsync(CancellationToken cancellationToken = default)
    {
        var ports = await _js.InvokeAsync<JsPort[]>("amiitag.serial.getPorts", cancellationToken);
        return ports.Select(p => new NfcReaderInfo(p.Id, p.Name)).ToList();
    }

    public async Task<NfcReaderInfo?> RequestPortAsync(CancellationToken cancellationToken = default)
    {
        var port = await _js.InvokeAsync<JsPort?>("amiitag.serial.requestPort", cancellationToken);
        return port == null ? null : new NfcReaderInfo(port.Id, port.Name);
    }

    public async Task<ISerialPortStream> OpenAsync(string portId, int baudRate, CancellationToken cancellationToken = default)
    {
        var stream = new WebSerialPortStream(_js, portId);
        try
        {
            await _js.InvokeVoidAsync("amiitag.serial.open", cancellationToken, portId, baudRate, stream.Reference);
        }
        catch (JSException ex)
        {
            stream.Reference.Dispose();
            throw new NfcException($"Couldn't open the serial port: {ex.Message}", ex);
        }

        return stream;
    }

    private sealed record JsPort(string Id, string Name);
}
