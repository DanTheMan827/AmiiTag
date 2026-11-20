namespace AmiiTag.Nfc.Pn532;

/// <summary>PN532 HSU framing (UM0701-02, section 6.2). Owns the serial stream.</summary>
public sealed class Pn532Uart : IAsyncDisposable
{
    private readonly ISerialPortStream _port;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _closed;
    private readonly byte[] _one = new byte[1];
    private int _receivedBytes;

    public Pn532Uart(ISerialPortStream port) => _port = port;

    public bool IsClosed => _closed;

    public async Task WakeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            // Drain before waking. A wake byte followed by an abort/drain interval is not the C106 startup
            // sequence: the chip is still in LowVbat until SAMConfiguration enters normal mode.
            // CommandAsync sends the long HSU preamble and SAMConfiguration together in one UART write.
            using var drain = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            drain.CancelAfter(TimeSpan.FromMilliseconds(100));
            var buffer = new byte[280];
            try
            {
                while (await _port.ReadAsync(buffer, 0, buffer.Length, drain.Token) > 0) { }
                throw new IOException("The PN532 serial port closed.");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        }
        catch
        {
            await CloseCoreAsync();
            throw;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Sends an ACK frame to the PN532 (the host's confirmation that completes SetSerialBaudRate).
    /// </summary>
    public async Task SendAckAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            await _port.WriteAsync([0x00, 0x00, 0xFF, 0x00, 0xFF, 0x00], cancellationToken);
        }
        catch
        {
            await CloseCoreAsync();
            throw;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Validates both ACK and response, including extended frames. A failed or interrupted exchange closes the
    /// stream, so a late response can never be consumed by the next command. The provider reopens on next connect.
    /// </summary>
    public async Task<byte[]> CommandAsync(byte command, byte[] data, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var frame = BuildFrame(command, data);
        // AN10609_2 section 2.4.2: C106 LowVbat wakeup must accompany SAMConfiguration. Use a long preamble
        // (also used by libnfc) and keep it in the same write as the frame, including after a module reset.
        var wake = command == 0x14;
        if (wake) frame = [0x55, 0x55, .. new byte[14], .. frame];
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(2));
            var stage = "ACK";
            _receivedBytes = 0;
            try
            {
                System.Diagnostics.Debug.WriteLine($"PN532 UART TX: command 0x{command:X2}, {data.Length} parameter bytes, HSU wake preamble: {wake}.");
                await _port.WriteAsync(frame, deadline.Token);
                var ack = await ReadFrameAsync(deadline.Token);
                if (ack.Length != 0) throw new NfcException("The PN532 did not acknowledge the command.");
                System.Diagnostics.Debug.WriteLine($"PN532 UART RX: ACK for command 0x{command:X2}.");
                stage = "command response";
                var response = await ReadFrameAsync(deadline.Token);
                if (response.Length < 2 || response[0] != 0xD5 || response[1] != unchecked((byte)(command + 1)))
                    throw new NfcException($"Unexpected PN532 response to command 0x{command:X2}.");
                System.Diagnostics.Debug.WriteLine($"PN532 UART RX: response 0x{response[1]:X2}, {response.Length - 2} parameter bytes.");
                return response[2..];
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"The PN532 did not answer command 0x{command:X2}: waiting for {stage}; {_receivedBytes} UART bytes received.");
            }
        }
        catch
        {
            await CloseCoreAsync();
            throw;
        }
        finally { _gate.Release(); }
    }

    private static byte[] BuildFrame(byte command, byte[] data)
    {
        // The PN532 command buffer holds TFI, command and at most 264 parameter bytes.
        if (data.Length > 264) throw new ArgumentOutOfRangeException(nameof(data));
        var length = data.Length + 2;
        var header = length <= 255
            ? new byte[] { 0, 0, 0xFF, (byte)length, unchecked((byte)-length) }
            : new byte[] { 0, 0, 0xFF, 0xFF, 0xFF, (byte)(length >> 8), (byte)length,
                unchecked((byte)-((length >> 8) + (length & 0xFF))) };
        var sum = 0xD4 + command;
        foreach (var value in data) sum += value;
        return [.. header, 0xD4, command, .. data, unchecked((byte)-sum), 0];
    }

    private async Task<byte[]> ReadFrameAsync(CancellationToken token)
    {
        // Preamble can contain arbitrary bytes; 00 FF is the synchronization pattern.
        var zero = false;
        while (true)
        {
            var value = await ReadByteAsync(token);
            if (zero && value == 0xFF) break;
            zero = value == 0;
        }
        var length = (int)await ReadByteAsync(token);
        var lcs = await ReadByteAsync(token);
        if (length == 0 && lcs == 0xFF)
        {
            if (await ReadByteAsync(token) != 0) throw new NfcException("Invalid PN532 ACK.");
            return [];
        }
        if (length == 0xFF && lcs == 0)
            throw new NfcException("The PN532 returned a NACK.");
        if (length == 0xFF && lcs == 0xFF)
        {
            var high = await ReadByteAsync(token);
            var low = await ReadByteAsync(token);
            if (((high + low + await ReadByteAsync(token)) & 0xFF) != 0)
                throw new NfcException("Invalid PN532 extended length checksum.");
            length = (high << 8) | low;
        }
        else if (((length + lcs) & 0xFF) != 0)
            throw new NfcException("Invalid PN532 length checksum.");
        if (length is < 1 or > 267) throw new NfcException("Invalid PN532 frame length.");
        var body = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var count = await _port.ReadAsync(body, offset, length - offset, token);
            if (count <= 0) throw new IOException("The PN532 serial port closed.");
            offset += count;
            _receivedBytes += count;
        }
        var sum = (int)await ReadByteAsync(token);
        foreach (var value in body) sum += value;
        if ((sum & 0xFF) != 0 || await ReadByteAsync(token) != 0)
            throw new NfcException("Invalid PN532 frame checksum or postamble.");
        return body;
    }

    private async Task<byte> ReadByteAsync(CancellationToken token)
    {
        if (await _port.ReadAsync(_one, 0, 1, token) != 1)
            throw new IOException("The PN532 serial port closed.");
        ++_receivedBytes;
        return _one[0];
    }

    private async ValueTask CloseCoreAsync()
    {
        if (_closed) return;
        _closed = true;
        try { await _port.DisposeAsync(); }
        catch { /* Preserve the original communication failure. */ }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try { await CloseCoreAsync(); }
        finally { _gate.Release(); }
    }
}
