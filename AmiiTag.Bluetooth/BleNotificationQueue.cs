using System.Text;
using System.Threading.Channels;

namespace AmiiTag.Bluetooth;

/// <summary>
/// Buffers notifications from a characteristic so request/response protocols can wait for replies
/// without missing packets that arrive early.
/// </summary>
public sealed class BleNotificationQueue : IDisposable
{
    private readonly IBleCharacteristic _characteristic;
    private Channel<byte[]> _channel = CreateChannel();

    public BleNotificationQueue(IBleCharacteristic characteristic)
    {
        _characteristic = characteristic;
        _characteristic.ValueUpdated += OnValueUpdated;
    }

    /// <summary>
    /// Raised for every packet, in addition to queueing it.
    /// </summary>
    public event EventHandler<byte[]>? PacketReceived;

    private static Channel<byte[]> CreateChannel() => Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
    });

    private void OnValueUpdated(object? sender, byte[] value)
    {
        var copy = (byte[])value.Clone();
        _channel.Writer.TryWrite(copy);
        PacketReceived?.Invoke(this, copy);
    }

    /// <summary>
    /// Drops anything received so far.
    /// </summary>
    public void Clear()
    {
        while (_channel.Reader.TryRead(out _))
        {
        }
    }

    /// <summary>
    /// Waits for the next packet.
    /// </summary>
    public async Task<byte[]> ReceiveAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            return await _channel.Reader.ReadAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The device didn't respond.");
        }
    }

    /// <summary>
    /// Collects exactly <paramref name="count"/> bytes from as many packets as it takes.
    /// The timeout applies to the gap between packets.
    /// </summary>
    public async Task<byte[]> ReceiveBytesAsync(int count, TimeSpan timeoutPerPacket, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        var output = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var packet = await ReceiveAsync(timeoutPerPacket, cancellationToken);
            var take = Math.Min(packet.Length, count - offset);
            Array.Copy(packet, 0, output, offset, take);
            offset += take;
            progress?.Report(offset);
        }

        return output;
    }

    /// <summary>
    /// Accumulates packets until the ASCII text contains <paramref name="marker"/>.
    /// </summary>
    public async Task<string> WaitForTextAsync(string marker, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var text = new StringBuilder();
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                throw new TimeoutException($"\"{marker}\" was not received.");
            }

            var packet = await ReceiveAsync(remaining, cancellationToken);
            text.Append(Encoding.ASCII.GetString(packet));
            if (text.ToString().Contains(marker, StringComparison.Ordinal))
            {
                return text.ToString();
            }
        }
    }

    public void Dispose()
    {
        _characteristic.ValueUpdated -= OnValueUpdated;
        _channel.Writer.TryComplete();
    }
}
