using System.Threading.Channels;
using AmiiTag.Nfc.Mfrc522;
using Microsoft.JSInterop;

namespace AmiiTag.WebAssembly.Services;

internal sealed class WebSerialPortStream : ISerialPortTransactionStream
{
    private readonly IJSRuntime _js;
    private readonly string _portId;
    private readonly Channel<byte[]> _received = Channel.CreateUnbounded<byte[]>();
    private byte[]? _pending;
    private int _pendingOffset;

    public WebSerialPortStream(IJSRuntime js, string portId)
    {
        _js = js;
        _portId = portId;
        Reference = DotNetObjectReference.Create(this);
    }

    public DotNetObjectReference<WebSerialPortStream> Reference { get; }

    [JSInvokable]
    public void OnData(byte[] data) => _received.Writer.TryWrite(data);

    [JSInvokable]
    public void OnClosed() => _received.Writer.TryComplete();

    public async Task WriteAsync(byte[] data, CancellationToken cancellationToken = default) =>
        await _js.InvokeVoidAsync("amiitag.serial.write", cancellationToken, _portId, data);

    public async Task<byte[]> TransactAsync(byte[] request, int responseLength, TimeSpan timeout)
    {
        // Keep the outstanding UART request entirely in JS. Cancelling an interop await would not cancel
        // the physical write/read, and could leave the next register batch consuming this one's late reply.
        try
        {
            var result = await _js.InvokeAsync<SerialTransactionResult>("amiitag.serial.transact", _portId,
                request, responseLength, timeout.TotalMilliseconds);
            if (result.Error == "timeout")
            {
                throw new TimeoutException($"The MFRC522 didn't answer ({result.Received} of {responseLength} bytes).");
            }
            if (result.Error != null)
            {
                throw new IOException($"Web Serial transaction failed: {result.Error}");
            }
            return result.Data ?? throw new IOException("Web Serial returned no transaction data.");
        }
        catch (JSException ex)
        {
            throw new IOException($"Web Serial transaction failed: {ex.Message}", ex);
        }
    }

    private sealed class SerialTransactionResult
    {
        public byte[]? Data { get; set; }
        public string? Error { get; set; }
        public int Received { get; set; }
    }

    public async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken = default)
    {
        if (_pending == null)
        {
            try
            {
                _pending = await _received.Reader.ReadAsync(cancellationToken);
            }
            catch (ChannelClosedException)
            {
                throw new IOException("The serial port was closed.");
            }

            _pendingOffset = 0;
        }

        var take = Math.Min(count, _pending.Length - _pendingOffset);
        Array.Copy(_pending, _pendingOffset, buffer, offset, take);
        _pendingOffset += take;
        if (_pendingOffset >= _pending.Length)
        {
            _pending = null;
        }

        return take;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _js.InvokeVoidAsync("amiitag.serial.close", _portId);
        }
        catch (JSException)
        {
        }

        Reference.Dispose();
    }
}
