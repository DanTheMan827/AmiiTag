namespace AmiiTag.Nfc.Mfrc522;

/// <summary>
/// Register access to an MFRC522 wired to a USB-serial adapter in UART mode (datasheet section 8.1.3), with no
/// microcontroller in between. A read is one address byte (bit 7 set) answered with the register value; a write is
/// the address byte (bit 7 clear) followed by the value, answered with the address as an acknowledgement.
/// Several reads or writes are sent back to back and their answers collected together, which keeps FIFO transfers fast.
/// </summary>
/// <remarks>
/// A batch that has been sent is always finished (or timed out) before cancellation is honoured: abandoning it would
/// leave the chip's answers in the serial buffer and every later answer would be read one batch out of step. After a
/// timeout or a bad acknowledgement, anything left on the line is drained so the next batch starts in step.
/// </remarks>
public sealed class Mfrc522Uart : IAsyncDisposable
{
    private readonly byte[] _buffer = new byte[256];

    public Mfrc522Uart(ISerialPortStream port, int baudRate)
    {
        Port = port;
        BaudRate = baudRate;
    }

    public ISerialPortStream Port { get; }

    public int BaudRate { get; }

    /// <summary>
    /// How long to wait for the chip's answers to one batch.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMilliseconds(250);

    public async Task<byte> ReadAsync(byte register, CancellationToken cancellationToken = default) =>
        (await ReadManyAsync([register], cancellationToken))[0];

    public async Task<byte[]> ReadManyAsync(IReadOnlyList<byte> registers, CancellationToken cancellationToken = default)
    {
        var output = new byte[registers.Count];
        for (var start = 0; start < registers.Count; start += 64)
        {
            var count = Math.Min(64, registers.Count - start);
            var request = new byte[count];
            for (var i = 0; i < count; i++)
            {
                request[i] = (byte)(0x80 | (registers[start + i] & 0x3F));
            }

            cancellationToken.ThrowIfCancellationRequested();
            (await TransactAsync(request, count)).CopyTo(output, start);
        }

        return output;
    }

    public Task WriteAsync(byte register, byte value, CancellationToken cancellationToken = default) =>
        WriteManyAsync([(register, value)], cancellationToken);

    public async Task WriteManyAsync(IReadOnlyList<(byte Register, byte Value)> writes, CancellationToken cancellationToken = default)
    {
        for (var start = 0; start < writes.Count; start += 32)
        {
            var count = Math.Min(32, writes.Count - start);
            var request = new byte[count * 2];
            for (var i = 0; i < count; i++)
            {
                request[i * 2] = (byte)(writes[start + i].Register & 0x3F);
                request[i * 2 + 1] = writes[start + i].Value;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var echoes = await TransactAsync(request, count);
            for (var i = 0; i < count; i++)
            {
                if (echoes[i] != (writes[start + i].Register & 0x3F))
                {
                    await DrainAsync(TimeSpan.FromMilliseconds(30));
                    throw new NfcException($"The MFRC522 didn't acknowledge a register write (expected {writes[start + i].Register:X2}, got {echoes[i]:X2}).");
                }
            }
        }
    }

    public async Task SetBitsAsync(byte register, byte mask, CancellationToken cancellationToken = default) =>
        await WriteAsync(register, (byte)(await ReadAsync(register, cancellationToken) | mask), cancellationToken);

    public async Task ClearBitsAsync(byte register, byte mask, CancellationToken cancellationToken = default) =>
        await WriteAsync(register, (byte)(await ReadAsync(register, cancellationToken) & ~mask), cancellationToken);

    public Task<byte[]> ReadFifoAsync(int count, CancellationToken cancellationToken = default) =>
        count == 0 ? Task.FromResult(Array.Empty<byte>()) : ReadManyAsync(Enumerable.Repeat(Mfrc522Reg.FifoData, count).ToArray(), cancellationToken);

    /// <summary>
    /// Discards anything waiting on the port (line noise after opening, answers to an earlier timed out request).
    /// </summary>
    public async Task DrainAsync(TimeSpan quiet, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(quiet);
            try
            {
                await Port.ReadAsync(_buffer, 0, _buffer.Length, timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Sends one batch and collects its answers. Deliberately not cancellable once started (only the timeout ends it).
    /// </summary>
    private async Task<byte[]> TransactAsync(byte[] request, int answerLength)
    {
        await Port.WriteAsync(request);

        var output = new byte[answerLength];
        var received = 0;
        using var timeout = new CancellationTokenSource(Timeout);
        try
        {
            while (received < answerLength)
            {
                received += await Port.ReadAsync(output, received, answerLength - received, timeout.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Late answers would put every later batch out of step: clear them before the next one.
            await DrainAsync(TimeSpan.FromMilliseconds(30));
            throw new TimeoutException($"The MFRC522 didn't answer ({received} of {answerLength} bytes).");
        }

        return output;
    }

    public ValueTask DisposeAsync() => Port.DisposeAsync();
}
