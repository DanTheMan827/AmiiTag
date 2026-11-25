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

    protected async Task<bool> SetupUartAsync(CancellationToken cancellationToken, int requestedMtu = 247)
    {
        _write = await TryGetCharacteristicAsync(BleUuids.NordicUartService, BleUuids.NordicUartWrite, cancellationToken);
        var notify = await TryGetCharacteristicAsync(BleUuids.NordicUartService, BleUuids.NordicUartNotify, cancellationToken);
        if (_write == null || notify == null)
        {
            return false;
        }

        // Packet devices need enough room for a complete command in one characteristic write.
        NegotiatedMtu = await Connection.RequestMtuAsync(requestedMtu, cancellationToken);

        _responses = new BleNotificationQueue(notify);
        await notify.StartNotificationsAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Writes one packet and waits for the device's acknowledgement notification.
    /// </summary>
    protected async Task<byte[]> SendAndWaitAsync(
        byte[] packet,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null,
        bool requireNonEmpty = false)
    {
        var responses = _responses ?? throw new BleException("The device isn't connected.");
        await _lock.WaitAsync(cancellationToken);
        try
        {
            responses.Clear();
            await _write!.WriteAsync(packet, true, cancellationToken);
            byte[] response;
            do
            {
                response = await responses.ReceiveAsync(timeout ?? AckTimeout, cancellationToken);
            }
            while (requireNonEmpty && response.Length == 0);

            return response;
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
