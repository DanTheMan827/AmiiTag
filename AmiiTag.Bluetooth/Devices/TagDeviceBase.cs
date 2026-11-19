using AmiiTag.Tags;

namespace AmiiTag.Bluetooth.Devices;

public abstract class TagDeviceBase : ITagDevice
{
    protected TagDeviceBase(IBleConnection connection)
    {
        Connection = connection;
        Connection.Disconnected += OnConnectionDisconnected;
    }

    protected IBleConnection Connection { get; }

    public virtual string Name => Connection.Device.DisplayName;

    public abstract TagDeviceKind Kind { get; }

    public string? Firmware { get; protected set; }

    public BleDeviceInfo Info => Connection.Device;

    public bool IsConnected => Connection.IsConnected;

    public event EventHandler? Disconnected;

    public abstract Task WriteTagAsync(TagDump dump, int? slot = null, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets up characteristics and checks the device answers like this kind of device.
    /// Returns false (without throwing) when it doesn't, so detection can try the next kind.
    /// </summary>
    public abstract Task<bool> InitializeAsync(CancellationToken cancellationToken = default);

    public virtual async Task DisconnectAsync()
    {
        try
        {
            await Connection.DisconnectAsync();
        }
        catch
        {
        }
    }

    /// <summary>
    /// Releases listeners without disconnecting, so another driver can probe the same connection.
    /// </summary>
    public virtual void Detach() => Connection.Disconnected -= OnConnectionDisconnected;

    protected virtual void OnConnectionDisconnected(object? sender, EventArgs e) => Disconnected?.Invoke(this, EventArgs.Empty);

    public virtual async ValueTask DisposeAsync()
    {
        Connection.Disconnected -= OnConnectionDisconnected;
        await DisconnectAsync();
        GC.SuppressFinalize(this);
    }

    protected async Task<IBleCharacteristic?> TryGetCharacteristicAsync(Guid service, Guid characteristic, CancellationToken cancellationToken)
    {
        try
        {
            return await Connection.GetCharacteristicAsync(service, characteristic, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Rebuilds a dump from the 80 bytes of slot information the pucks return.
    /// </summary>
    protected static TagDump DumpFromSlotInfo(ReadOnlySpan<byte> info)
    {
        var tag = new byte[TagDump.FullLength];
        info[0..8].CopyTo(tag.AsSpan(0));
        info[8..20].CopyTo(tag.AsSpan(16));
        info[20..40].CopyTo(tag.AsSpan(32));
        info[40..48].CopyTo(tag.AsSpan(84));
        info[48..80].CopyTo(tag.AsSpan(96));

        // The slot info omits BCC1; recompute it so the UID displays correctly. It's only a summary (no lock bytes, CC,
        // signatures or app data), so it's described by AmiiboLookup.GetSlot*, never validated as a tag image.
        tag[8] = (byte)(tag[4] ^ tag[5] ^ tag[6] ^ tag[7]);
        return new TagDump(tag);
    }
}
