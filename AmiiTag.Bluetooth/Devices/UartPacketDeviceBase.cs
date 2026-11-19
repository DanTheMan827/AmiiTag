namespace AmiiTag.Bluetooth.Devices;

/// <summary>
/// Shared plumbing for devices that take one command per write on the Nordic UART service
/// and acknowledge each with a notification (AmiiboLink, AmiLoop).
/// </summary>
public abstract class UartPacketDeviceBase : TagDeviceBase
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IBleCharacteristic? _write;
    private BleNotificationQueue? _responses;

    protected UartPacketDeviceBase(IBleConnection connection) : base(connection)
    {
    }

    protected TimeSpan AckTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The MTU the platform reported after asking for a larger one, or 0 if it doesn't say (Web Bluetooth).
    /// </summary>
    protected int NegotiatedMtu { get; private set; }

    protected async Task<bool> SetupUartAsync(CancellationToken cancellationToken)
    {
        _write = await TryGetCharacteristicAsync(BleUuids.NordicUartService, BleUuids.NordicUartWrite, cancellationToken);
        var notify = await TryGetCharacteristicAsync(BleUuids.NordicUartService, BleUuids.NordicUartNotify, cancellationToken);
        if (_write == null || notify == null)
        {
            return false;
        }

        // AmiLoop packets are up to 135 bytes; ask for room so they aren't split into long writes.
        NegotiatedMtu = await Connection.RequestMtuAsync(247, cancellationToken);

        _responses = new BleNotificationQueue(notify);
        await notify.StartNotificationsAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Writes one packet and waits for the device's acknowledgement notification.
    /// </summary>
    protected async Task<byte[]> SendAndWaitAsync(byte[] packet, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        var responses = _responses ?? throw new BleException("The device isn't connected.");
        await _lock.WaitAsync(cancellationToken);
        try
        {
            responses.Clear();
            await _write!.WriteAsync(packet, true, cancellationToken);
            return await responses.ReceiveAsync(timeout ?? AckTimeout, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Writes one packet without waiting for a reply.
    /// </summary>
    protected async Task SendAsync(byte[] packet, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await (_write ?? throw new BleException("The device isn't connected.")).WriteAsync(packet, true, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public override void Detach()
    {
        _responses?.Dispose();
        _responses = null;
        base.Detach();
    }

    public override async Task DisconnectAsync()
    {
        _responses?.Dispose();
        _responses = null;
        await base.DisconnectAsync();
    }
}
